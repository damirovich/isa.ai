using ISC.AI.Identity.Skid;
using Shouldly;
using Sodium;

namespace ISC.AI.UnitTests.Security;

/// <summary>
/// Совместимость проверки паролей с хешами СКИД (Э3-08, ТБ-013): СКИД хеширует libsodium Argon2id
/// (PHC-строка) — наша проверка обязана принимать эти строки и отвергать неверный пароль.
/// </summary>
public sealed class SkidPasswordHashTests
{
    [Fact(DisplayName = "Argon2id PHC-строка (формат СКИД): верный пароль принимается, неверный — нет")]
    public void Verify_accepts_correct_and_rejects_wrong_password()
    {
        // Хеш создаётся тем же способом, что в СКИД (libsodium ArgonHashString → $argon2id$...).
        // Interactive-стойкость — только ради скорости теста; формат PHC одинаков для всех уровней.
        var phc = PasswordHash.ArgonHashString("Qwerty1!", PasswordHash.StrengthArgon.Interactive);

        phc.ShouldStartWith("$argon2id$");
        SkidPasswordHash.Verify(phc, "Qwerty1!").ShouldBeTrue();
        SkidPasswordHash.Verify(phc, "Qwerty1?").ShouldBeFalse();
    }

    [Fact(DisplayName = "Холостая проверка (выравнивание времени) не бросает и ничего не подтверждает")]
    public void Dummy_verification_does_not_throw()
    {
        Should.NotThrow(() => SkidPasswordHash.VerifyDummy("любой пароль"));
    }
}
