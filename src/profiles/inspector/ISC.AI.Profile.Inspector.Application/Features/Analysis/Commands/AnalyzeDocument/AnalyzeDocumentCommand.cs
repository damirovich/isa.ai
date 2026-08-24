using FluentValidation;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Enums;
using ISC.AI.Abstractions.Grounding;
using ISC.AI.Abstractions.Rag;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Inspector.Application.Features.Generation;
using Mediator;

namespace ISC.AI.Profile.Inspector.Application.Features.Analysis;

/// <summary>
/// Правовой, организационный и управленческий анализ документа (ТФ-НПА-03, §5.2.3): оценить
/// вставленный текст на соответствие нормам корпуса. Роль — Analysis (вдумчивая, ADR-0011).
/// Выводы — ПРОЕКТ для человека (ТБ-042); ссылки проходят грунтовку (GATE-2).
/// </summary>
/// <param name="Text">Анализируемый текст документа.</param>
public sealed record AnalyzeDocumentCommand(string Text)
    : IRequest<ResponseDto<GenerateReferenceResult>>, IGroundedScenario
{
    // НАМЕРЕННО не IAuditableRequest: аудит генерации пишет оркестратор ядра (иначе двойная запись).

    /// <summary>Сколько символов начала документа уходит в тему ИЗВЛЕЧЕНИЯ (сам документ — не запрос).</summary>
    public const int RetrievalContextLength = 500;

    /// <inheritdoc cref="AnalyzeDocumentCommand" />
    public sealed class Handler(
        IGroundedGenerator generator,
        IAccessContextProvider accessContextProvider,
        IAnalyzeDocumentPromptRenderer promptRenderer)
        : IRequestHandler<AnalyzeDocumentCommand, ResponseDto<GenerateReferenceResult>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<GenerateReferenceResult>> Handle(
            AnalyzeDocumentCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            var access = await accessContextProvider.GetCurrentAsync(cancellationToken);

            // Тема ИЗВЛЕЧЕНИЯ — начало документа: нормы ищутся по его предмету.
            var context = command.Text.Length <= RetrievalContextLength
                ? command.Text
                : command.Text[..RetrievalContextLength];
            var response = await generator.GenerateAsync(
                new GroundedRequest(
                    $"Нормативные требования по предмету документа: {context}",
                    ModelRole.Analysis,
                    TaskPrompt: promptRenderer.Render(command)),
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
                RequiresHumanReview: true, // ТБ-042.
                AllCitationsConfirmed: response.Grounding.AllConfirmed,
                Citations: response.Grounding.Citations,
                ResultClassification: response.ResultClassification));
        }
    }
}

/// <summary>Текст обязателен; предел — как у Редактора.</summary>
public sealed class AnalyzeDocumentValidator : AbstractValidator<AnalyzeDocumentCommand>
{
    /// <inheritdoc cref="AnalyzeDocumentValidator" />
    public AnalyzeDocumentValidator() =>
        RuleFor(c => c.Text).NotEmpty().WithMessage("Вставьте текст документа.")
            .MaximumLength(100_000);
}
