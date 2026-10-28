using System.Buffers.Binary;
using System.Security.Cryptography;

namespace ISC.AI.Backup;

/// <summary>
/// Шифрование файлов резервной копии открытым ключом Заказчика (ТБ-082, ADR-0063) — потоком, без открытого текста на диске.
/// </summary>
/// <remarks>
/// <para>Формат файла (числа — little-endian):</para>
/// <code>
/// "ISCBAK01" (8) | отпечаток ключа (32) | длина обёрнутого ключа (2) | ключ AES-256, обёрнутый RSA-OAEP-SHA256 |
/// префикс одноразовых номеров (8) | порции…
/// порция: длина открытого текста (4) | признак последней (1) | шифртекст | тег GCM (16)
/// </code>
/// <para>
/// Каждой порции (до <see cref="ChunkSize"/> байт) — свой одноразовый номер: префикс файла + номер порции (big-endian).
/// В проверяемые данные (AAD) входят SHA-256 заголовка, номер порции и признак последней: переставить, подменить,
/// дописать или отрезать порции незаметно нельзя — расшифровка обрывается ошибкой. Ключ AES свой у каждого файла и
/// после работы затирается в памяти.
/// </para>
/// <para>Средства — <c>System.Security.Cryptography</c>; это не сертифицированное СКЗИ (ТБ-061, см. ADR-0063).</para>
/// </remarks>
public static class BackupCipher
{
    /// <summary>Размер порции открытого текста, байт.</summary>
    public const int ChunkSize = 1 << 20;

    private const int KeySize = 32;
    private const int TagSize = 16;
    private const int NoncePrefixSize = 8;
    private const int FingerprintSize = 32;

    private static ReadOnlySpan<byte> Magic => "ISCBAK01"u8;

    /// <summary>Шифрует поток целиком; возвращает объём открытого текста.</summary>
    public static async Task<long> EncryptAsync(Stream plaintext, Stream output, RSA publicKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(publicKey);

        var key = RandomNumberGenerator.GetBytes(KeySize);
        try
        {
            var prefix = RandomNumberGenerator.GetBytes(NoncePrefixSize);
            var header = BuildHeader(BackupKeys.Fingerprint(publicKey), publicKey.Encrypt(key, RSAEncryptionPadding.OaepSHA256), prefix);
            await output.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            var headerHash = SHA256.HashData(header);

            using var aes = new AesGcm(key, TagSize);
            var current = new byte[ChunkSize];
            var next = new byte[ChunkSize];
            var cipher = new byte[ChunkSize];
            var tag = new byte[TagSize];
            var record = new byte[5];
            var total = 0L;
            var index = 0u;

            // Чтение на порцию вперёд: признак «последняя» известен до шифрования порции.
            var read = await ReadFullAsync(plaintext, current, cancellationToken).ConfigureAwait(false);
            while (true)
            {
                var nextRead = read == ChunkSize ? await ReadFullAsync(plaintext, next, cancellationToken).ConfigureAwait(false) : 0;
                var last = nextRead == 0;

                aes.Encrypt(Nonce(prefix, index), current.AsSpan(0, read), cipher.AsSpan(0, read), tag, Aad(headerHash, index, last));
                BinaryPrimitives.WriteInt32LittleEndian(record, read);
                record[4] = last ? (byte)1 : (byte)0;
                await output.WriteAsync(record, cancellationToken).ConfigureAwait(false);
                await output.WriteAsync(cipher.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                await output.WriteAsync(tag, cancellationToken).ConfigureAwait(false);

                total += read;
                if (last)
                {
                    return total;
                }

                index = checked(index + 1);
                (current, next) = (next, current);
                read = nextRead;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>
    /// Расшифровывает поток целиком; возвращает объём открытого текста. Ключ — тот, чей отпечаток в заголовке.
    /// </summary>
    /// <exception cref="InvalidDataException">Не файл копии, файл оборван или к нему дописано лишнее.</exception>
    /// <exception cref="CryptographicException">Ключ не тот или содержимое изменено.</exception>
    public static async Task<long> DecryptAsync(Stream input, Stream plaintext, RSA privateKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentNullException.ThrowIfNull(privateKey);

        var (fingerprint, wrapped, prefix, header) = await ReadHeaderCoreAsync(input, cancellationToken).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(fingerprint, BackupKeys.Fingerprint(privateKey)))
        {
            throw new CryptographicException(
                $"Копия зашифрована другим ключом: отпечаток копии {Convert.ToHexString(fingerprint)}, введённого ключа {BackupKeys.FingerprintHex(privateKey)}.");
        }

        var key = privateKey.Decrypt(wrapped, RSAEncryptionPadding.OaepSHA256);
        try
        {
            var headerHash = SHA256.HashData(header);
            using var aes = new AesGcm(key, TagSize);
            var cipher = new byte[ChunkSize];
            var clear = new byte[ChunkSize];
            var tag = new byte[TagSize];
            var record = new byte[5];
            var total = 0L;
            var index = 0u;
            while (true)
            {
                if (await ReadFullAsync(input, record, cancellationToken).ConfigureAwait(false) != record.Length)
                {
                    throw new InvalidDataException("Файл копии оборван: нет последней порции.");
                }

                var length = BinaryPrimitives.ReadInt32LittleEndian(record);
                if (length is < 0 or > ChunkSize || record[4] > 1)
                {
                    throw new InvalidDataException("Файл копии повреждён: неверная запись порции.");
                }

                var last = record[4] == 1;
                if (await ReadFullAsync(input, cipher.AsMemory(0, length), cancellationToken).ConfigureAwait(false) != length
                    || await ReadFullAsync(input, tag, cancellationToken).ConfigureAwait(false) != TagSize)
                {
                    throw new InvalidDataException("Файл копии оборван посреди порции.");
                }

                aes.Decrypt(Nonce(prefix, index), cipher.AsSpan(0, length), tag, clear.AsSpan(0, length), Aad(headerHash, index, last));
                await plaintext.WriteAsync(clear.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
                total += length;
                if (last)
                {
                    if (input.ReadByte() >= 0)
                    {
                        throw new InvalidDataException("Файл копии повреждён: после последней порции есть данные.");
                    }

                    return total;
                }

                index = checked(index + 1);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>Отпечаток ключа из заголовка файла копии — для проверки манифеста без закрытого ключа.</summary>
    /// <exception cref="InvalidDataException">Не файл копии.</exception>
    public static async Task<string> ReadFingerprintAsync(Stream input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        var (fingerprint, _, _, _) = await ReadHeaderCoreAsync(input, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(fingerprint);
    }

    private static byte[] BuildHeader(byte[] fingerprint, byte[] wrappedKey, byte[] prefix)
    {
        var header = new byte[Magic.Length + FingerprintSize + 2 + wrappedKey.Length + NoncePrefixSize];
        var span = header.AsSpan();
        Magic.CopyTo(span);
        fingerprint.CopyTo(span[Magic.Length..]);
        BinaryPrimitives.WriteUInt16LittleEndian(span[(Magic.Length + FingerprintSize)..], checked((ushort)wrappedKey.Length));
        wrappedKey.CopyTo(span[(Magic.Length + FingerprintSize + 2)..]);
        prefix.CopyTo(span[(Magic.Length + FingerprintSize + 2 + wrappedKey.Length)..]);
        return header;
    }

    private static async Task<(byte[] Fingerprint, byte[] Wrapped, byte[] Prefix, byte[] Header)> ReadHeaderCoreAsync(
        Stream input, CancellationToken cancellationToken)
    {
        var fixedPart = new byte[Magic.Length + FingerprintSize + 2];
        if (await ReadFullAsync(input, fixedPart, cancellationToken).ConfigureAwait(false) != fixedPart.Length
            || !fixedPart.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            throw new InvalidDataException("Не файл резервной копии ISC.AI (нет сигнатуры ISCBAK01).");
        }

        var wrappedLength = BinaryPrimitives.ReadUInt16LittleEndian(fixedPart.AsSpan(Magic.Length + FingerprintSize));
        var rest = new byte[wrappedLength + NoncePrefixSize];
        if (await ReadFullAsync(input, rest, cancellationToken).ConfigureAwait(false) != rest.Length)
        {
            throw new InvalidDataException("Файл копии оборван в заголовке.");
        }

        return (
            fixedPart.AsSpan(Magic.Length, FingerprintSize).ToArray(),
            rest.AsSpan(0, wrappedLength).ToArray(),
            rest.AsSpan(wrappedLength).ToArray(),
            [.. fixedPart, .. rest]);
    }

    private static byte[] Nonce(byte[] prefix, uint index)
    {
        var nonce = new byte[NoncePrefixSize + 4];
        prefix.CopyTo(nonce, 0);
        BinaryPrimitives.WriteUInt32BigEndian(nonce.AsSpan(NoncePrefixSize), index);
        return nonce;
    }

    private static byte[] Aad(byte[] headerHash, uint index, bool last)
    {
        var aad = new byte[headerHash.Length + 5];
        headerHash.CopyTo(aad, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(aad.AsSpan(headerHash.Length), index);
        aad[^1] = last ? (byte)1 : (byte)0;
        return aad;
    }

    private static Task<int> ReadFullAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken) =>
        ReadFullAsync(stream, buffer.AsMemory(), cancellationToken);

    private static async Task<int> ReadFullAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken) =>
        await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
}
