using ISC.AI.Modules.Admin.Application.Features.Accounts;
using ISC.AI.Modules.Admin.Application.Features.Clearances;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Admin;

/// <summary>
/// Правила формы смены собственного пароля (ТБ-013): длина вместо требований к составу символов.
/// </summary>
/// <remarks>
/// Тест переехал сюда вместе с валидатором (ADR-0023). Раньше эти же случаи проверялись у профиля, и
/// у каждого профиля — своей копией; теперь правило одно на платформу, и проверять его надо там, где
/// оно живёт, иначе смена формата пароля в пакете осталась бы незамеченной обоими профилями.
/// </remarks>
public sealed class ChangeOwnPasswordValidatorTests
{
    private readonly ChangeOwnPasswordValidator _validator = new();

    [Fact(DisplayName = "Корректная команда проходит")]
    public void Accepts_valid_command() =>
        _validator.Validate(new ChangeOwnPasswordCommand("старый-пароль", "новый длинный пароль"))
            .IsValid.ShouldBeTrue();

    [Fact(DisplayName = "Пустой текущий пароль отклоняется")]
    public void Rejects_empty_current_password() =>
        _validator.Validate(new ChangeOwnPasswordCommand("", "новый длинный пароль")).IsValid.ShouldBeFalse();

    [Fact(DisplayName = "Новый пароль короче минимальной длины отклоняется")]
    public void Rejects_short_new_password() =>
        _validator.Validate(new ChangeOwnPasswordCommand(
            "старый-пароль", new string('x', ChangeOwnPasswordValidator.MinPasswordLength - 1)))
            .IsValid.ShouldBeFalse();

    [Fact(DisplayName = "Новый пароль ровно минимальной длины проходит")]
    public void Accepts_new_password_of_minimum_length() =>
        _validator.Validate(new ChangeOwnPasswordCommand(
            "старый-пароль", new string('x', ChangeOwnPasswordValidator.MinPasswordLength)))
            .IsValid.ShouldBeTrue();

    [Fact(DisplayName = "Новый пароль длиннее 200 символов отклоняется")]
    public void Rejects_overlong_new_password() =>
        _validator.Validate(new ChangeOwnPasswordCommand("старый-пароль", new string('x', 201))).IsValid.ShouldBeFalse();
}

/// <summary>
/// Правила формы выдачи допуска (ТБ-011/020/021) — теперь общие для всех профилей платформы.
/// </summary>
/// <remarks>
/// ГЛАВНЫЙ СЛУЧАЙ ЗДЕСЬ — пустой список подразделений: он РАЗРЕШЁН формой, потому что означает «не
/// видно ничего» (default-deny решётки, ТБ-021), а не ошибку ввода. Приняв его за ошибку, форма
/// закрыла бы единственный способ отозвать область видимости, оставив допуск по грифу.
/// </remarks>
public sealed class SetUserClearanceValidatorTests
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
