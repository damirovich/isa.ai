using System.Security.Cryptography;

namespace ISC.AI.Backup;

/// <summary>
/// Шифрование файлов резервной копии открытым ключом Заказчика (ТБ-082, ADR-0063) — потоком, без открытого текста на диске.
/// </summary>
/// <remarks>
/// Схема и формат — <see cref="SealedFile"/> с сигнатурой копии «ISCBAK01» (<see cref="SealedFormat.Backup"/>): тот же
/// механизм шифрует пакеты обмена между офисами (ADR-0068), а сигнатура не даёт принять один файл за другой. Байты копии
/// не изменились с ADR-0063 — прежние копии восстанавливаются как раньше.
/// </remarks>
public static class BackupCipher
{
    /// <summary>Размер порции открытого текста, байт.</summary>
    public const int ChunkSize = SealedFile.ChunkSize;

    /// <summary>Шифрует поток целиком; возвращает объём открытого текста.</summary>
    public static Task<long> EncryptAsync(Stream plaintext, Stream output, RSA publicKey, CancellationToken cancellationToken = default) =>
        SealedFile.EncryptAsync(plaintext, output, publicKey, SealedFormat.Backup, cancellationToken);

    /// <summary>
    /// Расшифровывает поток целиком; возвращает объём открытого текста. Ключ — тот, чей отпечаток в заголовке.
    /// </summary>
    /// <exception cref="InvalidDataException">Не файл копии, файл оборван или к нему дописано лишнее.</exception>
    /// <exception cref="CryptographicException">Ключ не тот или содержимое изменено.</exception>
    public static Task<long> DecryptAsync(Stream input, Stream plaintext, RSA privateKey, CancellationToken cancellationToken = default) =>
        SealedFile.DecryptAsync(input, plaintext, privateKey, SealedFormat.Backup, cancellationToken);

    /// <summary>Отпечаток ключа из заголовка файла копии — для проверки манифеста без закрытого ключа.</summary>
    /// <exception cref="InvalidDataException">Не файл копии.</exception>
    public static Task<string> ReadFingerprintAsync(Stream input, CancellationToken cancellationToken = default) =>
        SealedFile.ReadFingerprintAsync(input, SealedFormat.Backup, cancellationToken);
}
