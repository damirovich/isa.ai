using ISC.AI.Profile.Investigation.Application.Features.Clearances;
using Shouldly;

namespace ISC.AI.UnitTests.Investigation;

/// <summary>Правила формы выдачи допуска (ТБ-011/020/021) профиля «Следствие».</summary>
public sealed class ClearanceValidatorTests
{
    private readonly SetUserClearanceValidator _validator = new();

    [Fact(DisplayName = "Корректная команда проходит")]
    public void Accepts_valid_command() =>
        _validator.Validate(new SetUserClearanceCommand(42, 2, [5, 7])).IsValid.ShouldBeTrue();

    [Fact(DisplayName = "Пустой список подразделений РАЗРЕШЁН — это default-deny, а не ошибка (ТБ-021)")]
    public void Accepts_empty_division_scope() =>
        _validator.Validate(new SetUserClearanceCommand(42, 0, [])).IsValid.ShouldBeTrue();

    [Fact(DisplayName = "Неположительный пользователь отклоняется")]
    public void Rejects_non_positive_user() =>
        _validator.Validate(new SetUserClearanceCommand(0, 1, [5])).IsValid.ShouldBeFalse();

    [Theory(DisplayName = "Гриф вне шкалы 0..9 отклоняется")]
    [InlineData(-1)]
    [InlineData(10)]
    public void Rejects_classification_outside_scale(short classification) =>
        _validator.Validate(new SetUserClearanceCommand(42, classification, [5])).IsValid.ShouldBeFalse();

    [Fact(DisplayName = "Неположительный номер подразделения отклоняется")]
    public void Rejects_non_positive_division_id() =>
        _validator.Validate(new SetUserClearanceCommand(42, 1, [5, 0])).IsValid.ShouldBeFalse();

    [Fact(DisplayName = "Отсутствующий список подразделений отклоняется")]
    public void Rejects_null_division_scope() =>
        _validator.Validate(new SetUserClearanceCommand(42, 1, null!)).IsValid.ShouldBeFalse();
}
