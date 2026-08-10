using FluentValidation;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Profile.Inspector.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Inspector.Application.Features.Divisions;

/// <summary>Валидатор создания подразделения (форма §4.2).</summary>
public sealed class CreateDivisionValidator : AbstractValidator<CreateDivisionCommand>
{
    /// <summary>Правила: имя обязательно ≤500; код ≤100.</summary>
    public CreateDivisionValidator()
    {
        RuleFor(c => c.Name).NotEmpty().WithMessage("Укажите наименование подразделения.").MaximumLength(500);
        RuleFor(c => c.Code).MaximumLength(100);
    }
}
