using System.Globalization;
using System.Security.Cryptography;
using ISC.AI.Abstractions.Storage;
using ISC.AI.Profile.Investigation.Domain.Services;

namespace ISC.AI.Profile.Investigation.Application.Features.AccessImport;

/// <summary>
/// Файл базы Access в хранилище (ADR-0043 п. 1): загрузка с SHA-256 и открытие на чтение со сверкой SHA-256 —
/// подменённый или повреждённый после загрузки файл не переносится (цепочка хранения, ТБ-077).
/// </summary>
/// <remarks>
/// Файлы лежат в категории <see cref="Category"/>, в подкаталоге оператора: по имени файла из запроса субъект
/// открывает только то, что загрузил сам. Сам файл — исходник переноса: в хранилище он остаётся как доказательство
/// того, что и откуда перенесено.
/// </remarks>
public sealed class AccessImportFiles(IFileStorage storage)
{
    /// <summary>Категория хранилища.</summary>
    public const string Category = "access-imports";

    /// <summary>Предел размера — предел самого формата Access (2 ГБ).</summary>
    public const long MaxBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>Отказ: файл не .accdb и не .mdb.</summary>
    public const string WrongExtensionMessage = "Загрузите файл базы Access: .accdb или .mdb.";

    /// <summary>Отказ: файл в хранилище изменился после загрузки.</summary>
    public const string ChangedMessage =
        "Файл базы в хранилище не совпадает с загруженным (контрольная сумма изменилась) — загрузите базу заново.";

    /// <summary>Расширения файлов базы.</summary>
    public static IReadOnlyList<string> Extensions { get; } = [".accdb", ".mdb"];

    /// <summary>Допустимое ли расширение у имени файла.</summary>
    public static bool HasDatabaseExtension(string? fileName) =>
        fileName is not null && Extensions.Contains(Path.GetExtension(fileName), StringComparer.OrdinalIgnoreCase);

    /// <summary>Сохраняет загруженную базу и считает её SHA-256 по ходу записи.</summary>
    /// <param name="content">Содержимое (читается один раз до конца).</param>
    /// <param name="fileName">Исходное имя файла.</param>
    /// <param name="userId">Оператор — подкаталог хранилища.</param>
    /// <param name="cancellationToken">Отмена.</param>
    public async Task<AccessDatabaseFile> SaveAsync(Stream content, string fileName, int userId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (!HasDatabaseExtension(fileName))
        {
            throw new AccessDatabaseReadException(WrongExtensionMessage);
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var hashing = new HashingReadStream(content, hash, MaxBytes);
        var stored = await storage.SaveAsync(
            hashing, Path.GetExtension(fileName).ToLowerInvariant(), Category, SubPath(userId), cancellationToken);
        return new AccessDatabaseFile(stored, Path.GetFileName(fileName), hashing.BytesRead, Convert.ToHexString(hash.GetHashAndReset()));
    }

    /// <summary>Открывает сохранённую базу на чтение, сверив содержимое с SHA-256 загрузки.</summary>
    /// <param name="reader">Чтение баз Access.</param>
    /// <param name="file">Загруженный файл.</param>
    /// <param name="userId">Оператор — подкаталог хранилища.</param>
    /// <param name="cancellationToken">Отмена.</param>
    public async Task<IAccessDatabase> OpenAsync(
        IAccessDatabaseReader reader, AccessDatabaseFile file, int userId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(file);

        Stream stored;
        try
        {
            stored = await storage.OpenReadAsync(file.StoredFileName, Category, SubPath(userId), cancellationToken);
        }
        catch (FileNotFoundException exception)
        {
            throw new AccessDatabaseReadException("Загруженный файл базы не найден в хранилище — загрузите базу заново.", exception);
        }

        await using (stored)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var hashing = new HashingReadStream(stored, hash, MaxBytes);
            var database = await reader.OpenAsync(hashing, Path.GetExtension(file.FileName), cancellationToken);

            // Драйвер получает копию целиком (поток дочитан до конца), поэтому сумма уже посчитана по всему файлу.
            if (!string.Equals(Convert.ToHexString(hash.GetHashAndReset()), file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                await database.DisposeAsync();
                throw new AccessDatabaseReadException(ChangedMessage);
            }

            return database;
        }
    }

    private static string SubPath(int userId) => userId.ToString(CultureInfo.InvariantCulture);

    /// <summary>Поток только на чтение, который считает хеш и длину прочитанного и не даёт прочитать больше предела.</summary>
    private sealed class HashingReadStream(Stream inner, IncrementalHash hash, long maxBytes) : Stream
    {
        public long BytesRead { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => BytesRead;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Account(buffer.AsSpan(offset, inner.Read(buffer, offset, count)));

        public override int Read(Span<byte> buffer) => Account(buffer[..inner.Read(buffer)]);

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            return Account(buffer.Span[..read]);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int Account(ReadOnlySpan<byte> data)
        {
            BytesRead += data.Length;
            if (BytesRead > maxBytes)
            {
                throw new AccessDatabaseReadException("Файл больше 2 ГБ — это не база Access (больше формат не допускает).");
            }

            hash.AppendData(data);
            return data.Length;
        }
    }
}
