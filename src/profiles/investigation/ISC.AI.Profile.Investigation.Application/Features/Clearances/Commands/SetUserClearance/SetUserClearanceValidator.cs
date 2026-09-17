using FluentValidation;

namespace ISC.AI.Profile.Investigation.Application.Features.Clearances;

/// <summary>Правила формы выдачи допуска.</summary>
public sealed class SetUserClearanceValidator : AbstractValidator<SetUserClearanceCommand>
{
    /// <summary>Верхняя граница шкалы грифов в интерфейсе (та же, что в форме дела).</summary>
    public const short MaxClassification = 9;

    /// <summary>Пользователь обязателен, гриф в пределах шкалы, номера подразделений положительные.</summary>
    public SetUserClearanceValidator()
    {
        RuleFor(c => c.UserId).GreaterThan(0);
        RuleFor(c => c.MaxClassification)
            .InclusiveBetween((short)0, MaxClassification)
            .WithMessage($"Гриф допуска — от 0 до {MaxClassification}.");
        // Пустой список РАЗРЕШЁН намеренно: «допуск есть, подразделений нет» — законное состояние
        // (default-deny, ТБ-021), им же отбирают доступ, не отзывая допуск целиком.
        RuleFor(c => c.DivisionIds)
            .NotNull()
            .Must(ids => ids is null || ids.All(id => id > 0))
                .WithMessage("Номер подразделения должен быть положительным.");
    }
}
