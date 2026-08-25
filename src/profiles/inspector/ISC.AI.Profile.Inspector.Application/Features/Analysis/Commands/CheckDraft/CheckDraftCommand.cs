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
/// Сверка ПРОЕКТА документа с действующей нормативной базой до подписания (ТФ-АНПА-02, §5.2.3):
/// коллизии, противоречия, непроверяемые/устаревшие ссылки. Роль — Analysis (вдумчивая, ADR-0011);
/// извлечение шире обычного (<see cref="CheckTopK"/> — как у сравнения НПА): для сверки нужен
/// охват базы, а не пара ближайших норм. Вывод — ПРОЕКТ заключения для человека (ТБ-042),
/// ссылки проходят грунтовку (GATE-2); сверка в пределах извлечённых фрагментов —
/// юридическую экспертизу не заменяет (оговорка зашита в промпт-шаблон).
/// </summary>
/// <param name="Text">Полный текст проекта документа.</param>
public sealed record CheckDraftCommand(string Text)
    : IRequest<ResponseDto<GenerateReferenceResult>>, IGroundedScenario
{
    // НАМЕРЕННО не IAuditableRequest: аудит генерации пишет оркестратор ядра (иначе двойная запись).

    /// <summary>Сколько символов начала проекта уходит в тему ИЗВЛЕЧЕНИЯ (сам проект — не запрос).</summary>
    public const int RetrievalContextLength = 500;

    /// <summary>Ширина извлечения — как у сравнения НПА (ТФ-НПА-04): сверке нужен охват базы.</summary>
    public const int CheckTopK = 16;

    /// <inheritdoc cref="CheckDraftCommand" />
    public sealed class Handler(
        IGroundedGenerator generator,
        IAccessContextProvider accessContextProvider,
        ICheckDraftPromptRenderer promptRenderer)
        : IRequestHandler<CheckDraftCommand, ResponseDto<GenerateReferenceResult>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<GenerateReferenceResult>> Handle(
            CheckDraftCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            var access = await accessContextProvider.GetCurrentAsync(cancellationToken);

            // Тема ИЗВЛЕЧЕНИЯ — начало проекта: нормы для сверки ищутся по его предмету.
            var context = command.Text.Length <= RetrievalContextLength
                ? command.Text
                : command.Text[..RetrievalContextLength];
            var response = await generator.GenerateAsync(
                new GroundedRequest(
                    $"Действующие нормативные требования по предмету проекта: {context}",
                    ModelRole.Analysis,
                    CheckTopK,
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
                RequiresHumanReview: true, // ТБ-042: заключение сверки — проект, решает человек.
                AllCitationsConfirmed: response.Grounding.AllConfirmed,
                Citations: response.Grounding.Citations,
                ResultClassification: response.ResultClassification));
        }
    }
}

/// <summary>Текст обязателен; предел — как у анализа документа.</summary>
public sealed class CheckDraftValidator : AbstractValidator<CheckDraftCommand>
{
    /// <inheritdoc cref="CheckDraftValidator" />
    public CheckDraftValidator() =>
        RuleFor(c => c.Text).NotEmpty().WithMessage("Вставьте текст проекта документа.")
            .MaximumLength(100_000);
}
