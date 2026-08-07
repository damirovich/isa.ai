using Sodium;

namespace ISC.AI.Persistence.Security;

/// <summary>
/// Хеширование и проверка паролей — Argon2id, PHC-строки libsodium (<c>crypto_pwhash_str</c>).
/// </summary>
/// <remarks>
/// Алгоритм и формат ВЫБРАНЫ НЕ ЗАНОВО: это те же Argon2id/PHC, которыми хеширует СКИД (та же
/// библиотека). Совпадение — обязательное условие переноса учёток без смены паролей пользователями
/// (Э4-35 §6.5): импортированный из СКИД хеш проверяется здесь как родной.
///
/// ИНВАРИАНТЫ (ТД-004, ТБ-043): пароль не логируется и не сохраняется ни в каком виде; при неизвестном
/// логине выполняется ХОЛОСТАЯ проверка (<see cref="VerifyDummy"/>), чтобы время ответа не выдавало
/// существование учётки — тот же класс утечек, что timing-канал GATE-1.
/// </remarks>
public static class PasswordHashing
{
    // Фиктивный PHC-хеш для выравнивания времени при неизвестном логине. Считается один раз лениво
    // (Argon2id намеренно медленный — сотни мс), пароль-заглушка значения не имеет.
    private static readonly Lazy<string> DummyHash = new(
        () => PasswordHash.ArgonHashString("iscai-timing-dummy", PasswordHash.StrengthArgon.Moderate),
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Хеширует пароль в PHC-строку Argon2id (для создания учётки и смены пароля).</summary>
    public static string Hash(string password) =>
        PasswordHash.ArgonHashString(password, PasswordHash.StrengthArgon.Moderate);

    /// <summary>
    /// Проверяет пароль против PHC-строки. Битый или чужого формата хеш — <see langword="false"/>,
    /// а не исключение: одна повреждённая строка не должна ронять вход всей системы.
    /// </summary>
    public static bool Verify(string? phcHash, string password)
    {
        if (string.IsNullOrEmpty(phcHash) || string.IsNullOrEmpty(password))
        {
            return false;
        }

        try
        {
            return PasswordHash.ArgonHashStringVerify(phcHash, password);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// «Холостая» проверка против фиктивного хеша — вызывается, когда логин не найден, чтобы отказ
    /// занимал сопоставимое время с проверкой настоящего пароля.
    /// </summary>
    public static void VerifyDummy(string password)
    {
        try
        {
            _ = PasswordHash.ArgonHashStringVerify(DummyHash.Value, password);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            // Выравнивание времени — вспомогательная мера; её сбой не должен влиять на результат входа.
        }
    }

    /// <summary>Новый штамп безопасности (смена пароля, блокировка) — инвалидация живых сессий (ТБ-016).</summary>
    public static string NewSecurityStamp() => Guid.NewGuid().ToString("N");
}
