using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Enums;
using ISC.AI.Abstractions.Grounding;
using ISC.AI.Abstractions.Rag;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Inspector.Application.Features.Generation;
using ISC.AI.Profile.Inspector.Domain.Services;
using Mediator;
using Scriban;

namespace ISC.AI.Profile.Inspector.Application.Features.Archive;

/// <summary>
/// ИИ-аналитика архива (ТФ-АРХ-03, §5.2.4): тренды к предыдущему периоду, повторяемость,
/// сравнение территориальных и линейных подразделений. Все ЧИСЛА считает код
/// (<see cref="ArchiveAnalyticsFacts"/> из <see cref="IRiskDataSource"/> за два смежных периода
/// + типы подразделений из справочника §4.2); ИИ вдумчивой ролью Analysis интерпретирует
/// закономерности. Результат — проект для человека (ТБ-042); конверт общий с Генератором.
/// </summary>
/// <param name="PeriodDays">Период анализа в днях (7–366); сравнение — с предыдущим таким же.</param>
public sealed record GenerateArchiveAnalyticsCommand(int PeriodDays = 90)
    : IRequest<ResponseDto<GenerateReferenceResult>>, IGroundedScenario
{
    // НАМЕРЕННО не IAuditableRequest: аудит генерации пишет оркестратор ядра (иначе двойная запись).

    /// <inheritdoc cref="GenerateArchiveAnalyticsCommand" />
    public sealed class Handler(
        IGroundedGenerator generator,
        IRiskDataSource riskDataSource,
        IDivisionAdminStore divisionStore,
        IAccessContextProvider accessContextProvider,
        Abstractions.AI.IPromptProvider promptProvider)
        : IRequestHandler<GenerateArchiveAnalyticsCommand, ResponseDto<GenerateReferenceResult>>
    {
        private readonly Template _template =
            Template.Parse(promptProvider.GetTaskPrompt("archive-analytics").Text);

        /// <inheritdoc />
        public async ValueTask<ResponseDto<GenerateReferenceResult>> Handle(
            GenerateArchiveAnalyticsCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            var access = await accessContextProvider.GetCurrentAsync(cancellationToken);

            // ЧИСЛА — детерминированно из БД за ДВА смежных периода (тренд без второй точки — не тренд).
            var days = Math.Clamp(command.PeriodDays, 7, 366);
            var to = DateOnly.FromDateTime(DateTime.UtcNow);
            var from = to.AddDays(-(days - 1));
            var previousTo = from.AddDays(-1);
            var previousFrom = previousTo.AddDays(-(days - 1));

            var current = await riskDataSource.GetDashboardAsync(from, to, filter: null, cancellationToken);
            var previous = await riskDataSource.GetDashboardAsync(previousFrom, previousTo, filter: null, cancellationToken);
            var risks = await riskDataSource.GetDivisionRisksAsync(from, to, cancellationToken);
            var remediation = await riskDataSource.GetRemediationAsync(from, to, cancellationToken);

            // Типы подразделений — из справочника §4.2 (разрез «территориальные/линейные» ТФ-АРХ-03).
            var divisions = await divisionStore.ListAsync(cancellationToken);
            var kinds = divisions.ToDictionary(d => d.Id, d => d.Kind);

            var facts = ArchiveAnalyticsFacts.Build(
                current, previous, risks, remediation.Divisions, kinds, days);
            var taskPrompt = _template.Render(new { period_days = days, facts });

            var response = await generator.GenerateAsync(
                // Роль Analysis — вдумчивая (ADR-0011): интерпретация закономерностей, не пересказ цифр.
                new GroundedRequest(
                    "внутренний контроль: повторяющиеся нарушения, динамика, причины",
                    ModelRole.Analysis,
                    TaskPrompt: taskPrompt),
                access,
                cancellationToken);

            if (string.IsNullOrWhiteSpace(response.Answer))
            {
                return ResponseDto<GenerateReferenceResult>.BadRequest(
                    "Модель вернула пустой ответ. Повторите попытку; если повторяется — "
                    + "увеличьте Llm:Generation:MaxOutputTokens.");
            }

            return ResponseDto<GenerateReferenceResult>.Ok(new GenerateReferenceResult(
                DraftText: response.Answer,
                RequiresHumanReview: true, // ТБ-042: аналитическую записку подписывает человек.
                AllCitationsConfirmed: response.Grounding.AllConfirmed,
                Citations: response.Grounding.Citations,
                ResultClassification: response.ResultClassification));
        }
    }
}
