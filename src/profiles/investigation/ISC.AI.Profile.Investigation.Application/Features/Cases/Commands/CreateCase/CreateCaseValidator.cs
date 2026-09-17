using FluentValidation;

namespace ISC.AI.Profile.Investigation.Application.Features.Cases;

/// <summary>Правила формы заведения дела (ТФ-ДЕЛ-01, ТБ-024).</summary>
public sealed class CreateCaseValidator : AbstractValidator<CreateCaseCommand>
{
    /// <summary>Верхняя граница шкалы грифов в интерфейсе (та же, что у допусков).</summary>
    public const short MaxClassification = 9;

    /// <summary>Номер ≤100 непустой; название ≤500; вид и гриф в шкале; подразделение обязательно (без умолчаний, ТБ-024).</summary>
    public CreateCaseValidator()
    {
        RuleFor(c => c.Number).NotEmpty().WithMessage("Укажите номер дела.").MaximumLength(100);
        RuleFor(c => c.Title).NotEmpty().WithMessage("Укажите название дела.").MaximumLength(500);
        RuleFor(c => c.Kind).IsInEnum().WithMessage("Неизвестный вид дела.");
        RuleFor(c => c.Classification)
            .InclusiveBetween((short)0, MaxClassification)
            .WithMessage($"Гриф дела — от 0 до {MaxClassification}.");
        RuleFor(c => c.DivisionId).GreaterThan(0).WithMessage("Укажите подразделение дела (ТБ-024).");
        RuleFor(c => c.InvestigatorUserId).GreaterThan(0).When(c => c.InvestigatorUserId is not null);
        RuleFor(c => c.Basis).MaximumLength(2000);
    }
}
