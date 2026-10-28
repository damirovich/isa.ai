using System.Security.Cryptography;

namespace ISC.AI.Backup;

/// <summary>
/// Ключи резервных копий (ТБ-082, ADR-0063): открытый RSA-ключ Заказчика шифрует копии, закрытый — у хранителя ключа и
/// вводится только при восстановлении. Отпечаток ключа — SHA-256 от SubjectPublicKeyInfo: по нему конфигурация
/// закрепляет ключ (пин, как у моделей — ТИ-004), а программа восстановления сверяет, тот ли ключ ввёл хранитель.
/// </summary>
public static class BackupKeys
{
    /// <summary>Наименьшая длина ключа RSA, бит: короче — копия не создаётся.</summary>
    public const int MinKeyBits = 3072;

    /// <summary>Длина ключа, которую создаёт <see cref="Generate"/>, бит.</summary>
    public const int GeneratedKeyBits = 4096;

    // Защита закрытого ключа паролем хранителя: PKCS#8, AES-256-CBC, ключ из пароля — PBKDF2-SHA256.
    private static readonly PbeParameters PrivateKeyProtection = new(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 600_000);

    /// <summary>Отпечаток ключа (SHA-256 от SubjectPublicKeyInfo), 32 байта.</summary>
    public static byte[] Fingerprint(RSA key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return SHA256.HashData(key.ExportSubjectPublicKeyInfo());
    }

    /// <summary>Отпечаток ключа шестнадцатеричной строкой (прописные буквы) — для конфигурации, манифеста и экрана.</summary>
    public static string FingerprintHex(RSA key) => Convert.ToHexString(Fingerprint(key));

    /// <summary>
    /// Открытый ключ из PEM-файла (<c>BEGIN PUBLIC KEY</c>). Короче <see cref="MinKeyBits"/> бит — отказ.
    /// </summary>
    /// <exception cref="InvalidOperationException">Файл не содержит ключ RSA нужной длины.</exception>
    public static RSA LoadPublicKey(string pemPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pemPath);
        var rsa = RSA.Create();
        try
        {
            rsa.ImportFromPem(File.ReadAllText(pemPath));
        }
        catch (ArgumentException exception)
        {
            rsa.Dispose();
            throw new InvalidOperationException($"В файле {Path.GetFullPath(pemPath)} нет открытого ключа RSA в формате PEM.", exception);
        }

        if (rsa.KeySize < MinKeyBits)
        {
            var bits = rsa.KeySize;
            rsa.Dispose();
            throw new InvalidOperationException($"Ключ резервных копий — {bits} бит; нужен RSA не короче {MinKeyBits} бит.");
        }

        return rsa;
    }

    /// <summary>
    /// Закрытый ключ из PEM-файла, защищённого паролем (<c>BEGIN ENCRYPTED PRIVATE KEY</c>). Пароль не сохраняется.
    /// </summary>
    /// <exception cref="CryptographicException">Неверный пароль или повреждённый файл.</exception>
    public static RSA LoadPrivateKey(string pemPath, ReadOnlySpan<char> password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pemPath);
        var rsa = RSA.Create();
        try
        {
            rsa.ImportFromEncryptedPem(File.ReadAllText(pemPath), password);
            return rsa;
        }
        catch
        {
            rsa.Dispose();
            throw;
        }
    }

    /// <summary>Новая пара ключей <see cref="GeneratedKeyBits"/> бит — создаёт хранитель ключа на своём рабочем месте.</summary>
    public static RSA Generate() => RSA.Create(GeneratedKeyBits);

    /// <summary>Открытый ключ в PEM — его передают Администратору для конфигурации.</summary>
    public static string ExportPublicPem(RSA key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return key.ExportSubjectPublicKeyInfoPem();
    }

    /// <summary>Закрытый ключ в PEM, зашифрованный паролем хранителя (PKCS#8).</summary>
    public static string ExportPrivatePem(RSA key, ReadOnlySpan<char> password)
    {
        ArgumentNullException.ThrowIfNull(key);
        return key.ExportEncryptedPkcs8PrivateKeyPem(password, PrivateKeyProtection);
    }
}
