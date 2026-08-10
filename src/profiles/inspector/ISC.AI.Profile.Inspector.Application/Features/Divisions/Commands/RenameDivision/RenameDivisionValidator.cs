using FluentValidation;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Profile.Inspector.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Inspector.Application.Features.Divisions;

/// <inheritdoc cref="CreateDivisionValidator" />
public sealed class RenameDivisionValidator : AbstractValidator<RenameDivisionCommand>
{
    /// <summary>Правила: идентификатор положительный; имя обязательно ≤500; код ≤100.</summary>
    public RenameDivisionValidator()
    {
        RuleFor(c => c.Id).GreaterThan(0);
        RuleFor(c => c.Name).NotEmpty().WithMessage("Укажите наименование подразделения.").MaximumLength(500);
        RuleFor(c => c.Code).MaximumLength(100);
    }
}
