using ISC.AI.Profile.Investigation.Application.Features.Accounts;
using Shouldly;

namespace ISC.AI.UnitTests.Investigation;

/// <summary>Правила смены собственного пароля: длина вместо требований к составу.</summary>
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
