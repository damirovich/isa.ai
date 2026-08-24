using FluentValidation;

namespace ISC.AI.Profile.Inspector.Application.Features.Violations;

/// <summary>Те же пределы, что при создании.</summary>
public sealed class UpdateViolationValidator : AbstractValidator<UpdateViolationCommand>
{
    /// <inheritdoc cref="UpdateViolationValidator" />
    public UpdateViolationValidator()
    {
        RuleFor(c => c.DivisionId).GreaterThan(0).WithMessage("Выберите подразделение.");
        RuleFor(c => c.CategoryId).GreaterThan(0).WithMessage("Выберите вид нарушения.");
        RuleFor(c => c.DetectedAt)
            .Must(d => d <= DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)))
            .WithMessage("Дата выявления не может быть в будущем.");
        RuleFor(c => c.SourceDocRef).MaximumLength(200);
        RuleFor(c => c.SourceAssignmentRef).MaximumLength(200);
        RuleFor(c => c.ReferenceDocRef).MaximumLength(200);
        RuleFor(c => c.Cause).MaximumLength(4000);
        RuleFor(c => c.Recommendation).MaximumLength(4000);
    }
}
