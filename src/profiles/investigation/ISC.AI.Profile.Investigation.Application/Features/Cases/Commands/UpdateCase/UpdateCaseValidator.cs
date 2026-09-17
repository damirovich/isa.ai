using FluentValidation;

namespace ISC.AI.Profile.Investigation.Application.Features.Cases;

/// <summary>Правила формы правки дела.</summary>
public sealed class UpdateCaseValidator : AbstractValidator<UpdateCaseCommand>
{
    /// <summary>Идентификатор положительный; название ≤500 непустое; вид в перечислении.</summary>
    public UpdateCaseValidator()
    {
        RuleFor(c => c.CaseId).GreaterThan(0);
        RuleFor(c => c.Title).NotEmpty().WithMessage("Укажите название дела.").MaximumLength(500);
        RuleFor(c => c.Kind).IsInEnum().WithMessage("Неизвестный вид дела.");
        RuleFor(c => c.InvestigatorUserId).GreaterThan(0).When(c => c.InvestigatorUserId is not null);
        RuleFor(c => c.Basis).MaximumLength(2000);
    }
}
