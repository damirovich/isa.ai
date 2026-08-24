using FluentValidation;

namespace ISC.AI.Profile.Inspector.Application.Features.Violations;

/// <summary>Пределы полей нарушения — те же, что в схеме (ссылки ≤ 200, тексты ≤ 4000).</summary>
public sealed class CreateViolationValidator : AbstractValidator<CreateViolationCommand>
{
    /// <inheritdoc cref="CreateViolationValidator" />
    public CreateViolationValidator()
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
        // Срок раньше даты выявления — опечатка ввода: автопросрочка сработала бы немедленно.
        RuleFor(c => c.RemediationDeadline)
            .Must((c, deadline) => deadline is null || deadline >= c.DetectedAt)
            .WithMessage("Срок устранения не может быть раньше даты выявления.");
    }
}
