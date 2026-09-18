using FluentValidation;
using ISC.AI.Modules.Media.Domain.Model;

namespace ISC.AI.Modules.Media.Application.Features.Verification;

/// <summary>Правила записи решения верификации (ТФ-ВЕР-01/03, ТБ-073): кандидат, стадия, исход, обоснование по методике, фигурант.</summary>
public sealed class RecordVerificationValidator : AbstractValidator<RecordVerificationCommand>
{
    /// <summary>Предел длины обоснования (интерфейс берёт <c>MaxLength</c> отсюда).</summary>
    public const int MaxRationaleLength = 4000;

    /// <summary>Текст отказа: подтверждение эксперта без фигуранта (ТФ-ВЕР-03).</summary>
    public const string PersonRequiredMessage = "Для подтверждения укажите фигуранта дела (ТФ-ВЕР-03).";

    /// <inheritdoc cref="RecordVerificationValidator" />
    public RecordVerificationValidator()
    {
        RuleFor(c => c.CandidateId).GreaterThan(0);
        RuleFor(c => c.Stage).IsInEnum();
        RuleFor(c => c.Verdict).IsInEnum();
        RuleFor(c => c.Rationale).NotEmpty().MaximumLength(MaxRationaleLength)
            .WithMessage("Обоснование решения обязательно (морфологические признаки по методике, ТФ-ВЕР-01).");
        RuleFor(c => c.PersonRef).GreaterThan(0).When(c => c.PersonRef is not null);

        // Слепота верификатора (ТФ-ВЕР-02): фигуранта привязывает только эксперт.
        RuleFor(c => c.PersonRef)
            .Null()
            .When(c => c.Stage == VerificationStage.Verifier)
            .WithMessage("Привязка к фигуранту выполняется только на стадии эксперта (ТФ-ВЕР-02).");

        // ТФ-ВЕР-03: «подтверждён» эксперта без фигуранта после второго «подтверждён» дал бы статус без
        // «появления» — молчаливое невыполнение требования; фигурант обязателен на стадии эксперта.
        RuleFor(c => c.PersonRef)
            .NotNull()
            .When(c => c.Stage == VerificationStage.Expert && c.Verdict == VerificationVerdict.Confirmed)
            .WithMessage(PersonRequiredMessage);
    }
}
