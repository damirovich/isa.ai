using FluentValidation;
using ISC.AI.Modules.Media.Domain.Model;

namespace ISC.AI.Modules.Media.Application.Features.Verification;

/// <summary>Правила записи решения верификации (ТФ-ВЕР-01, ТБ-073): кандидат, стадия, исход, обоснование по методике.</summary>
public sealed class RecordVerificationValidator : AbstractValidator<RecordVerificationCommand>
{
    /// <summary>Предел длины обоснования.</summary>
    public const int MaxRationaleLength = 4000;

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
    }
}
