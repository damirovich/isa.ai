using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Grounding;
using ISC.AI.Abstractions.Rag;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Inspector.Application.Features.Generation;
using Mediator;

namespace ISC.AI.Profile.Inspector.Application.Features.Editor;

/// <summary>
/// ИИ-правка документа по команде (ТФ-РЕД-01/02, §5.2.10): «переформулируй строже», «добавь норму»,
/// «приведи к стандарту» — тем же grounded-конвейером, что генерация. ГРУНТОВКА СОХРАНЯЕТСЯ:
/// новая редакция проходит валидатор ссылок целиком — правка не может протащить выдуманную норму
/// (GATE-2). Результат — ЧЕРНОВИК: принимает или отклоняет человек (ТБ-042); конверт общий
/// с Генератором (<see cref="GenerateReferenceResult"/>).
/// </summary>
/// <param name="Text">Исходный текст документа.</param>
/// <param name="Instruction">Команда правки.</param>
public sealed record ReviseDocumentCommand(string Text, string Instruction)
    : IRequest<ResponseDto<GenerateReferenceResult>>, IGroundedScenario
{
    // НАМЕРЕННО не IAuditableRequest: аудит генерации (AuditAction.Generate, с грифом результата
    // и списком фрагментов) пишет сам оркестратор ядра — иначе двойная запись в неизменяемый журнал.

    /// <summary>Сколько символов начала документа уходит в тему ИЗВЛЕЧЕНИЯ (контекст для поиска норм).</summary>
    public const int RetrievalContextLength = 300;

    /// <inheritdoc cref="ReviseDocumentCommand" />
    public sealed class Handler(
        IGroundedGenerator generator,
        IAccessContextProvider accessContextProvider,
        IEditorPromptRenderer promptRenderer)
        : IRequestHandler<ReviseDocumentCommand, ResponseDto<GenerateReferenceResult>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<GenerateReferenceResult>> Handle(
            ReviseDocumentCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            // Fail-closed: без контекста доступа провайдер бросает исключение, извлечение не начнётся.
            var access = await accessContextProvider.GetCurrentAsync(cancellationToken);

            // Тема ИЗВЛЕЧЕНИЯ — команда + начало документа: «добавь норму про сроки» должна найти
            // нормы про сроки В КОНТЕКСТЕ темы документа, а не документ целиком (он не запрос).
            var context = command.Text.Length <= RetrievalContextLength
                ? command.Text
                : command.Text[..RetrievalContextLength];
            var response = await generator.GenerateAsync(
                new GroundedRequest(
                    $"{command.Instruction}. Контекст документа: {context}",
                    TaskPrompt: promptRenderer.Render(command)),
                access,
                cancellationToken);

            // «Думающая» модель может потратить весь лимит вывода на размышления и вернуть ПУСТОЙ
            // текст (инцидент 2026-08-24: кнопка «Принять» затёрла документ пустотой). Пустая
            // редакция — не результат, а сбой: честный отказ вместо тихой потери текста.
            if (string.IsNullOrWhiteSpace(response.Answer))
            {
                return ResponseDto<GenerateReferenceResult>.BadRequest(
                    "Модель вернула пустой ответ (весь лимит вывода ушёл на размышления). "
                    + "Повторите попытку; если повторяется — сократите документ или увеличьте Llm:Generation:MaxOutputTokens.");
            }

            var result = new GenerateReferenceResult(
                DraftText: response.Answer,
                RequiresHumanReview: true, // HITL всегда (ТБ-042): правку принимает человек.
                AllCitationsConfirmed: response.Grounding.AllConfirmed,
                Citations: response.Grounding.Citations,
                ResultClassification: response.ResultClassification);
            return ResponseDto<GenerateReferenceResult>.Ok(result);
        }
    }
}
