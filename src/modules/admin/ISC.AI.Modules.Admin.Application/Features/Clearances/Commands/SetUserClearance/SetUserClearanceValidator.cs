using FluentValidation;
using ISC.AI.Abstractions.Security;

namespace ISC.AI.Modules.Admin.Application.Features.Clearances;

/// <summary>Правила формы выдачи допуска.</summary>
public sealed class SetUserClearanceValidator : AbstractValidator<SetUserClearanceCommand>
{
    /// <summary>Верхняя граница шкалы грифов — единая шкала платформы (ADR-0030, <see cref="ClassificationLevels"/>).</summary>
    public const short MaxClassification = ClassificationLevels.Max;

    /// <summary>Правила: пользователь обязателен, гриф в пределах шкалы, номера подразделений положительные.</summary>
    public SetUserClearanceValidator()
    {
        RuleFor(c => c.UserId).GreaterThan(0);
        RuleFor(c => c.MaxClassification)
            .InclusiveBetween((short)0, MaxClassification)
            .WithMessage("Гриф допуска — от «Без грифа» до «Особой важности».");

        // Пустой список РАЗРЕШЁН намеренно: «допуск есть, подразделений нет» — законное состояние
        // (default-deny, ТБ-021), им же отбирают доступ, не отзывая допуск целиком.
        RuleFor(c => c.DivisionIds)
            .NotNull()
            .Must(ids => ids is null || ids.All(id => id > 0))
                .WithMessage("Номер подразделения должен быть положительным.");
    }
}
