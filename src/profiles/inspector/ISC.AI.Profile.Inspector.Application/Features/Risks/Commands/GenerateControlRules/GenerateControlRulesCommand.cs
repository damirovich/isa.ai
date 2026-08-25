using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Enums;
using ISC.AI.Abstractions.Grounding;
using ISC.AI.Abstractions.Rag;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Inspector.Application.Features.Generation;
using ISC.AI.Profile.Inspector.Domain.Services;
using Mediator;
using Scriban;

namespace ISC.AI.Profile.Inspector.Application.Features.Risks;

/// <summary>
/// Проект правил и процедур внутреннего контроля с критериями оценки подразделений
/// (ТФ-РСК-02, §5.2.5): показатели и процедуры — те, что СИСТЕМА реально считает
/// (<see cref="ControlRulesFacts"/>); ИИ быстрой ролью Draft оформляет их в положение,
/// НЕ назначая пороговых чисел (место «порог: ______» заполняет человек).
/// Результат — проект (ТБ-042); конверт общий с Генератором, судьба — реестр методик/экспорт.
/// </summary>
/// <param name="PeriodDays">Период для картины применения в днях (7–366).</param>
public sealed record GenerateControlRulesCommand(int PeriodDays = 90)
    : IRequest<ResponseDto<GenerateReferenceResult>>, IGroundedScenario
{
    // НАМЕРЕННО не IAuditableRequest: аудит генерации пишет оркестратор ядра (иначе двойная запись).

    /// <inheritdoc cref="GenerateControlRulesCommand" />
    public sealed class Handler(
        IGroundedGenerator generator,
        IRiskDataSource riskDataSource,
        IAccessContextProvider accessContextProvider,
        Abstractions.AI.IPromptProvider promptProvider)
        : IRequestHandler<GenerateControlRulesCommand, ResponseDto<GenerateReferenceResult>>
    {
        private readonly Template _template =
            Template.Parse(promptProvider.GetTaskPrompt("control-rules").Text);

        /// <inheritdoc />
        public async ValueTask<ResponseDto<GenerateReferenceResult>> Handle(
            GenerateControlRulesCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            var access = await accessContextProvider.GetCurrentAsync(cancellationToken);

            // Показатели и картина применения — детерминированно из БД.
            var days = Math.Clamp(command.PeriodDays, 7, 366);
            var to = DateOnly.FromDateTime(DateTime.UtcNow);
            var from = to.AddDays(-(days - 1));
            var risks = await riskDataSource.GetDivisionRisksAsync(from, to, cancellationToken);

            var facts = ControlRulesFacts.Build(risks, days);
            var taskPrompt = _template.Render(new { period_days = days, facts });

            var response = await generator.GenerateAsync(
                // Роль Draft — быстрая (без размышлений, ADR-0011): показатели готовы, оформить в положение.
                new GroundedRequest(
                    "правила внутреннего контроля, критерии оценки подразделений",
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
                RequiresHumanReview: true, // ТБ-042: правила утверждает человек.
                AllCitationsConfirmed: response.Grounding.AllConfirmed,
                Citations: response.Grounding.Citations,
                ResultClassification: response.ResultClassification));
        }
    }
}
