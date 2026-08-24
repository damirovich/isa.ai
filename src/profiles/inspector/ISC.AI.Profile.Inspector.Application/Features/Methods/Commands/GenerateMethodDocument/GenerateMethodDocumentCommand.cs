using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Grounding;
using ISC.AI.Abstractions.Rag;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Inspector.Application.Features.Generation;
using Mediator;

namespace ISC.AI.Profile.Inspector.Application.Features.Methods;

/// <summary>
/// Генерация методического документа (ТФ-МЕТ-01, §5.2.9): программа проверки, чек-лист, инструкция,
/// памятка или учебный материал по типу и объекту проверки — тем же grounded-конвейером, что справка
/// Генератора (Э4-03). Результат — ЧЕРНОВИК для проверки человеком (ТБ-042); правовые ссылки проходят
/// грунтовку (GATE-2). Конверт результата общий с Генератором (<see cref="GenerateReferenceResult"/>).
/// </summary>
/// <param name="ArtifactKind">Вид документа (программа проверки / чек-лист / инструкция / памятка / учебный материал).</param>
/// <param name="InspectionType">Тип проверки (комплексная / целевая / контрольная / внеплановая или свой).</param>
/// <param name="Scope">Объект проверки (подразделение и/или направление работы).</param>
/// <param name="Extra">Дополнительные указания инспектора (опц.).</param>
public sealed record GenerateMethodDocumentCommand(
    string ArtifactKind,
    string InspectionType,
    string Scope,
    string? Extra = null) : IRequest<ResponseDto<GenerateReferenceResult>>, IGroundedScenario
{
    // НАМЕРЕННО не IAuditableRequest: аудит генерации (AuditAction.Generate, с грифом результата
    // и списком фрагментов) пишет сам оркестратор ядра — иначе двойная запись в неизменяемый журнал.

    /// <inheritdoc cref="GenerateMethodDocumentCommand" />
    public sealed class Handler(
        IGroundedGenerator generator,
        IAccessContextProvider accessContextProvider,
        IMethodPromptRenderer promptRenderer)
        : IRequestHandler<GenerateMethodDocumentCommand, ResponseDto<GenerateReferenceResult>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<GenerateReferenceResult>> Handle(
            GenerateMethodDocumentCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            // Fail-closed: без контекста доступа провайдер бросает исключение, извлечение не начнётся.
            var access = await accessContextProvider.GetCurrentAsync(cancellationToken);

            // Query — для семантического ИЗВЛЕЧЕНИЯ норм (объект и тип проверки задают тему),
            // задачный промпт — как строить сам документ.
            var query = $"{command.InspectionType} проверка: {command.Scope}."
                + (string.IsNullOrWhiteSpace(command.Extra) ? string.Empty : $" {command.Extra}");
            var response = await generator.GenerateAsync(
                new GroundedRequest(query, TaskPrompt: promptRenderer.Render(command)),
                access,
                cancellationToken);

            // «Думающая» модель может потратить весь лимит вывода на размышления и вернуть ПУСТОЙ текст —
            // это сбой, а не черновик: честный отказ (инцидент 2026-08-24, как в Редакторе).
            if (string.IsNullOrWhiteSpace(response.Answer))
            {
                return ResponseDto<GenerateReferenceResult>.BadRequest(
                    "Модель вернула пустой ответ (весь лимит вывода ушёл на размышления). "
                    + "Повторите попытку; если повторяется — увеличьте Llm:Generation:MaxOutputTokens.");
            }

            var result = new GenerateReferenceResult(
                DraftText: response.Answer,
                RequiresHumanReview: true, // HITL всегда (ТБ-042): методику утверждает человек.
                AllCitationsConfirmed: response.Grounding.AllConfirmed,
                Citations: response.Grounding.Citations,
                ResultClassification: response.ResultClassification);
            return ResponseDto<GenerateReferenceResult>.Ok(result);
        }
    }
}
