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

namespace ISC.AI.Profile.Inspector.Application.Features.Methods;

/// <summary>
/// Проект плана работы инспекции / дорожной карты (ТФ-МЕТ-02, §5.2.9): проблемные зоны считает
/// код — факт-блок ОБЩИЙ с отчётом руководству (<see cref="ManagementReportFacts"/> из
/// <see cref="IRiskDataSource"/>); ИИ быстрой ролью Draft раскладывает их в план мероприятий,
/// НЕ назначая календарных сроков (место «в срок до ______» заполняет человек).
/// Результат — проект (ТБ-042); конверт общий с Генератором, судьба — реестр методик/экспорт.
/// </summary>
/// <param name="PeriodDays">Период анализа проблемных зон в днях (7–366).</param>
public sealed record GenerateWorkPlanCommand(int PeriodDays = 90)
    : IRequest<ResponseDto<GenerateReferenceResult>>, IGroundedScenario
{
    // НАМЕРЕННО не IAuditableRequest: аудит генерации пишет оркестратор ядра (иначе двойная запись).

    /// <inheritdoc cref="GenerateWorkPlanCommand" />
    public sealed class Handler(
        IGroundedGenerator generator,
        IRiskDataSource riskDataSource,
        IAccessContextProvider accessContextProvider,
        Abstractions.AI.IPromptProvider promptProvider)
        : IRequestHandler<GenerateWorkPlanCommand, ResponseDto<GenerateReferenceResult>>
    {
        private readonly Template _template =
            Template.Parse(promptProvider.GetTaskPrompt("work-plan").Text);

        /// <inheritdoc />
        public async ValueTask<ResponseDto<GenerateReferenceResult>> Handle(
            GenerateWorkPlanCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            var access = await accessContextProvider.GetCurrentAsync(cancellationToken);

            // Основания плана — детерминированно из БД (те же источники, что экраны).
            var days = Math.Clamp(command.PeriodDays, 7, 366);
            var to = DateOnly.FromDateTime(DateTime.UtcNow);
            var from = to.AddDays(-(days - 1));
            var dashboard = await riskDataSource.GetDashboardAsync(from, to, cancellationToken);
            var risks = await riskDataSource.GetDivisionRisksAsync(from, to, cancellationToken);
            var remediation = await riskDataSource.GetRemediationAsync(from, to, cancellationToken);

            var facts = ManagementReportFacts.Build(dashboard, risks, remediation.Divisions, days);
            var taskPrompt = _template.Render(new { period_days = days, facts });

            var response = await generator.GenerateAsync(
                // Роль Draft — быстрая (без размышлений, ADR-0011): проблемные зоны готовы.
                new GroundedRequest(
                    "планирование внутреннего контроля, устранение нарушений",
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
                RequiresHumanReview: true, // ТБ-042: план утверждает человек.
                AllCitationsConfirmed: response.Grounding.AllConfirmed,
                Citations: response.Grounding.Citations,
                ResultClassification: response.ResultClassification));
        }
    }
}
