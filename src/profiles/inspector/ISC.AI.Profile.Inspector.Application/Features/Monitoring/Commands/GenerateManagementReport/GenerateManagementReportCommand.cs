using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Enums;
using ISC.AI.Abstractions.Grounding;
using ISC.AI.Abstractions.Rag;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Inspector.Application.Features.Generation;
using ISC.AI.Profile.Inspector.Domain.Services;
using Mediator;
using Scriban;

namespace ISC.AI.Profile.Inspector.Application.Features.Monitoring;

/// <summary>
/// Аналитический отчёт для руководства (ТФ-МОН-02, §5.2.6): все ЧИСЛА считает код из живого учёта
/// (те же источники, что экраны — <see cref="IRiskDataSource"/>), ИИ быстрой ролью Draft пишет
/// только связующий текст. Результат — проект для человека (ТБ-042); конверт общий с Генератором.
/// </summary>
/// <param name="PeriodDays">Период отчёта в днях (7–366, как у экранов).</param>
public sealed record GenerateManagementReportCommand(int PeriodDays = 90)
    : IRequest<ResponseDto<GenerateReferenceResult>>, IGroundedScenario
{
    // НАМЕРЕННО не IAuditableRequest: аудит генерации пишет оркестратор ядра (иначе двойная запись).

    /// <inheritdoc cref="GenerateManagementReportCommand" />
    public sealed class Handler(
        IGroundedGenerator generator,
        IRiskDataSource riskDataSource,
        IAccessContextProvider accessContextProvider,
        Abstractions.AI.IPromptProvider promptProvider)
        : IRequestHandler<GenerateManagementReportCommand, ResponseDto<GenerateReferenceResult>>
    {
        // Шаблон парсится один раз на время жизни обработчика (transient — на запрос; дёшево).
        private readonly Template _template =
            Template.Parse(promptProvider.GetTaskPrompt("management-report").Text);

        /// <inheritdoc />
        public async ValueTask<ResponseDto<GenerateReferenceResult>> Handle(
            GenerateManagementReportCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            var access = await accessContextProvider.GetCurrentAsync(cancellationToken);

            // ЧИСЛА — детерминированно из БД, до всякого ИИ (ТФ-МОН-02: аналитика — код, текст — модель).
            var days = Math.Clamp(command.PeriodDays, 7, 366);
            var to = DateOnly.FromDateTime(DateTime.UtcNow);
            var from = to.AddDays(-(days - 1));
            var dashboard = await riskDataSource.GetDashboardAsync(from, to, cancellationToken);
            var risks = await riskDataSource.GetDivisionRisksAsync(from, to, cancellationToken);
            var remediation = await riskDataSource.GetRemediationAsync(from, to, cancellationToken);

            var facts = ManagementReportFacts.Build(dashboard, risks, remediation.Divisions, days);
            var taskPrompt = _template.Render(new { period_days = days, facts });

            var response = await generator.GenerateAsync(
                // Роль Draft — быстрая (без размышлений, ADR-0011): числа готовы, думать не над чем.
                new GroundedRequest(
                    "внутренний контроль: устранение нарушений, исполнительская дисциплина",
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
                RequiresHumanReview: true, // ТБ-042: докладную подписывает человек.
                AllCitationsConfirmed: response.Grounding.AllConfirmed,
                Citations: response.Grounding.Citations,
                ResultClassification: response.ResultClassification));
        }
    }
}
