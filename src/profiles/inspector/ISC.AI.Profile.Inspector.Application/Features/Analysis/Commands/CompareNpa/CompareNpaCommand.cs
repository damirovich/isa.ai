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
/// Сравнение НПА по теме (ТФ-НПА-04, §5.2.3.2): извлечь нормы разных актов по теме и выявить
/// противоречия и пробелы. Роль — Analysis (ВДУМЧИВАЯ, с размышлениями — ADR-0011): поиск
/// действительных расхождений — многошаговая задача, где раздумья оправданы. Выводы ИИ — ПРОЕКТ
/// для проверки человеком (ТБ-042); каждая ссылка проходит грунтовку (GATE-2). Конверт результата
/// общий с Генератором (<see cref="GenerateReferenceResult"/>).
/// </summary>
/// <param name="Topic">Тема сравнения (например, «сроки регистрации входящих документов»).</param>
public sealed record CompareNpaCommand(string Topic)
    : IRequest<ResponseDto<GenerateReferenceResult>>, IGroundedScenario
{
    // НАМЕРЕННО не IAuditableRequest: аудит генерации пишет оркестратор ядра (иначе двойная запись).

    /// <summary>Фрагментов для сравнения больше обычного: нужны нормы РАЗНЫХ актов по одной теме.</summary>
    public const int CompareTopK = 16;

    /// <inheritdoc cref="CompareNpaCommand" />
    public sealed class Handler(
        IGroundedGenerator generator,
        IAccessContextProvider accessContextProvider,
        ICompareNpaPromptRenderer promptRenderer)
        : IRequestHandler<CompareNpaCommand, ResponseDto<GenerateReferenceResult>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<GenerateReferenceResult>> Handle(
            CompareNpaCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            var access = await accessContextProvider.GetCurrentAsync(cancellationToken);
            var response = await generator.GenerateAsync(
                new GroundedRequest(
                    command.Topic, ModelRole.Analysis, TopK: CompareTopK,
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
                RequiresHumanReview: true, // ТБ-042: выводы анализа — проект, решение за человеком.
                AllCitationsConfirmed: response.Grounding.AllConfirmed,
                Citations: response.Grounding.Citations,
                ResultClassification: response.ResultClassification));
        }
    }
}

/// <summary>Тема сравнения обязательна и разумной длины.</summary>
public sealed class CompareNpaValidator : AbstractValidator<CompareNpaCommand>
{
    /// <inheritdoc cref="CompareNpaValidator" />
    public CompareNpaValidator() =>
        RuleFor(c => c.Topic).NotEmpty().WithMessage("Укажите тему сравнения.")
            .MinimumLength(3).MaximumLength(500);
}
