using Sodium;

namespace ISC.AI.Identity.Skid;

/// <summary>
/// Проверка паролей по Argon2id PHC-строкам СКИД (libsodium <c>crypto_pwhash_str</c>). Та же
/// библиотека, которой СКИД хеширует, — совместимость гарантирована форматом PHC.
/// </summary>
/// <remarks>
/// ИНВАРИАНТ (ТД-004, ТБ-043): пароль не логируется и не сохраняется; при неизвестном логине
/// выполняется проверка против фиктивного хеша (<see cref="VerifyDummy"/>), чтобы время ответа
/// не выдавало существование учётки (тот же класс утечек, что timing-канал GATE-1).
/// </remarks>
public static class SkidPasswordHash
{
    // Фиктивный PHC-хеш для выравнивания времени при неизвестном логине. Считается один раз лениво
    // (Argon2id намеренно медленный — ~сотни мс), пароль-заглушка значения не имеет.
    private static readonly Lazy<string> DummyHash = new(
        () => PasswordHash.ArgonHashString("iscai-timing-dummy", PasswordHash.StrengthArgon.Moderate),
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Проверяет пароль против PHC-строки Argon2id из БД СКИД.</summary>
    public static bool Verify(string phcHash, string password) =>
        PasswordHash.ArgonHashStringVerify(phcHash, password);

    /// <summary>
    /// «Холостая» проверка против фиктивного хеша — вызывается, когда логин не найден, чтобы отказ
    /// занимал сопоставимое время с проверкой настоящего пароля.
    /// </summary>
    public static void VerifyDummy(string password) =>
        _ = PasswordHash.ArgonHashStringVerify(DummyHash.Value, password);
}
