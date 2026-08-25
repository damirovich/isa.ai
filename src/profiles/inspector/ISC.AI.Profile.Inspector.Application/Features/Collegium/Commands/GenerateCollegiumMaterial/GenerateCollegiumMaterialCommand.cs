using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Enums;
using ISC.AI.Abstractions.Grounding;
using ISC.AI.Abstractions.Rag;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Inspector.Application.Features.Generation;
using ISC.AI.Profile.Inspector.Application.Features.Monitoring;
using ISC.AI.Profile.Inspector.Domain.Services;
using Mediator;
using Scriban;

namespace ISC.AI.Profile.Inspector.Application.Features.Collegium;

/// <summary>Вид материала коллегии (§5.2.8): что именно готовится к заседанию/совещанию.</summary>
public enum CollegiumMaterialKind
{
    /// <summary>Доклад на заседание коллегии (ТФ-КОЛ-01).</summary>
    Report = 1,

    /// <summary>Проект решения коллегии (ТФ-КОЛ-01): поручения без выдуманных сроков.</summary>
    DraftDecision = 2,

    /// <summary>Структурированные материалы к совещанию руководства (ТФ-КОЛ-02).</summary>
    Briefing = 3,
}

/// <summary>
/// Материалы коллегии (ТФ-КОЛ-01/02, §5.2.8): доклад, проект решения либо материалы к совещанию
/// руководства. Все ЧИСЛА считает код — факт-блок ОБЩИЙ с отчётом руководству
/// (<see cref="ManagementReportFacts"/>: те же источники, что экраны, — <see cref="IRiskDataSource"/>);
/// ИИ быстрой ролью Draft пишет только связующий текст под структуру выбранного вида.
/// Результат — проект для человека (ТБ-042); конверт общий с Генератором.
/// </summary>
/// <param name="Kind">Вид материала (доклад/проект решения/материалы к совещанию).</param>
/// <param name="PeriodDays">Период в днях (7–366, как у экранов).</param>
public sealed record GenerateCollegiumMaterialCommand(
    CollegiumMaterialKind Kind, int PeriodDays = 90)
    : IRequest<ResponseDto<GenerateReferenceResult>>, IGroundedScenario
{
    // НАМЕРЕННО не IAuditableRequest: аудит генерации пишет оркестратор ядра (иначе двойная запись).

    /// <inheritdoc cref="GenerateCollegiumMaterialCommand" />
    public sealed class Handler(
        IGroundedGenerator generator,
        IRiskDataSource riskDataSource,
        IAccessContextProvider accessContextProvider,
        Abstractions.AI.IPromptProvider promptProvider)
        : IRequestHandler<GenerateCollegiumMaterialCommand, ResponseDto<GenerateReferenceResult>>
    {
        // Все три шаблона парсятся при создании обработчика: опечатка в имени ресурса
        // всплывает на ПЕРВОМ обращении к модулю, а не при редком выборе конкретного вида.
        private readonly IReadOnlyDictionary<CollegiumMaterialKind, Template> _templates =
            new Dictionary<CollegiumMaterialKind, Template>
            {
                [CollegiumMaterialKind.Report] =
                    Template.Parse(promptProvider.GetTaskPrompt("collegium-report").Text),
                [CollegiumMaterialKind.DraftDecision] =
                    Template.Parse(promptProvider.GetTaskPrompt("collegium-decision").Text),
                [CollegiumMaterialKind.Briefing] =
                    Template.Parse(promptProvider.GetTaskPrompt("collegium-briefing").Text),
            };

        /// <inheritdoc />
        public async ValueTask<ResponseDto<GenerateReferenceResult>> Handle(
            GenerateCollegiumMaterialCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (!_templates.TryGetValue(command.Kind, out var template))
            {
                return ResponseDto<GenerateReferenceResult>.BadRequest("Неизвестный вид материала.");
            }

            var access = await accessContextProvider.GetCurrentAsync(cancellationToken);

            // ЧИСЛА — детерминированно из БД, до всякого ИИ (тот же факт-блок, что у ТФ-МОН-02).
            var days = Math.Clamp(command.PeriodDays, 7, 366);
            var to = DateOnly.FromDateTime(DateTime.UtcNow);
            var from = to.AddDays(-(days - 1));
            var dashboard = await riskDataSource.GetDashboardAsync(from, to, filter: null, cancellationToken);
            var risks = await riskDataSource.GetDivisionRisksAsync(from, to, cancellationToken);
            var remediation = await riskDataSource.GetRemediationAsync(from, to, cancellationToken);

            var facts = ManagementReportFacts.Build(dashboard, risks, remediation.Divisions, days);
            var taskPrompt = template.Render(new { period_days = days, facts });

            var response = await generator.GenerateAsync(
                // Роль Draft — быстрая (без размышлений, ADR-0011): числа готовы, думать не над чем.
                new GroundedRequest(
                    "внутренний контроль: итоги периода, исполнительская дисциплина, решения коллегии",
                    ModelRole.Draft,
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
                RequiresHumanReview: true, // ТБ-042: доклад и решение подписывает человек.
                AllCitationsConfirmed: response.Grounding.AllConfirmed,
                Citations: response.Grounding.Citations,
                ResultClassification: response.ResultClassification));
        }
    }
}
