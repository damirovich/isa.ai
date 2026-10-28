using System.Globalization;
using System.IO.Pipelines;
using System.Security.Cryptography;
using ISC.AI.Abstractions.Backup;
using Npgsql;

namespace ISC.AI.Backup;

/// <summary>Что копировать и куда.</summary>
/// <param name="TargetFolder">Каталог на изолированном носителе.</param>
/// <param name="Databases">Базы данных (по одной строке подключения на базу).</param>
/// <param name="FileRoots">Каталоги файлов (хранилище ядра и пакетов) — каждый своим архивом.</param>
/// <param name="PublicKey">Открытый ключ Заказчика.</param>
/// <param name="SystemVersion">Версия системы — в манифест.</param>
/// <param name="CreatedByUserId">Кто запустил.</param>
/// <param name="CreatedAt">Время копии (имя каталога и манифест).</param>
public sealed record BackupRequest(
    string TargetFolder,
    IReadOnlyList<NpgsqlConnectionStringBuilder> Databases,
    IReadOnlyList<BackupFileRoot> FileRoots,
    RSA PublicKey,
    string SystemVersion,
    int? CreatedByUserId,
    DateTimeOffset CreatedAt);

/// <summary>Готовая копия.</summary>
/// <param name="FolderName">Имя каталога копии на носителе.</param>
/// <param name="Manifest">Манифест.</param>
/// <param name="BytesWritten">Записано на носитель, байт.</param>
public sealed record BackupResult(string FolderName, BackupManifest Manifest, long BytesWritten);

/// <summary>
/// Создание резервной копии (ТБ-082, ADR-0063): каждая база — <c>pg_dump</c> потоком в шифрование, затем каждый каталог
/// файлов — архивом потоком в шифрование, затем манифест. Каталог пишется с окончанием <c>.partial</c> и переименовывается
/// последним шагом: оборванная копия за готовую не сойдёт; при сбое недописанный каталог удаляется.
/// </summary>
public sealed class BackupWriter(PgTools tools)
{
    /// <summary>Окончание имени каталога, пока копия пишется.</summary>
    public const string PartialSuffix = ".partial";

    /// <summary>Начало имени каталога копии.</summary>
    public const string FolderPrefix = "isc-backup-";

    /// <summary>Имя файла архива каталога файлов в копии.</summary>
    public static string StorageFileFor(string rootName) => "files-" + rootName + ".tar.enc";

    /// <summary>Имя каталога копии по времени.</summary>
    public static string FolderNameFor(DateTimeOffset createdAt) =>
        FolderPrefix + createdAt.ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture);

    /// <summary>Делает копию; ход — записанные на носитель байты.</summary>
    public async Task<BackupResult> WriteAsync(BackupRequest request, Action<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var folderName = FolderNameFor(request.CreatedAt);
        var final = Path.Combine(request.TargetFolder, folderName);
        var partial = final + PartialSuffix;
        if (Directory.Exists(final) || Directory.Exists(partial))
        {
            throw new IOException($"На носителе уже есть копия {folderName}.");
        }

        Directory.CreateDirectory(partial);
        var written = 0L;
        var files = new List<BackupFileEntry>();
        try
        {
            var databases = new List<BackupDatabaseEntry>();
            foreach (var database in request.Databases)
            {
                var file = "db-" + SafeName(database.Database ?? "db") + ".dump.enc";
                var serverVersion = await PgTools.ServerVersionAsync(database, cancellationToken).ConfigureAwait(false);
                var dumpProgram = await tools.ForServerAsync("pg_dump", database, cancellationToken).ConfigureAwait(false);
                var dumpVersion = await PgTools.VersionAsync(dumpProgram, cancellationToken).ConfigureAwait(false);
                var entry = await WriteFileAsync(partial, file, written, progress, async target =>
                    await tools.DumpAsync(database, dump => BackupCipher.EncryptAsync(dump, target, request.PublicKey, cancellationToken), cancellationToken)
                        .ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
                written += entry.Bytes;
                files.Add(entry);
                databases.Add(new BackupDatabaseEntry(database.Database ?? string.Empty, database.Host ?? string.Empty, database.Port, serverVersion, dumpVersion, file));
            }

            var storages = new List<BackupStorageEntry>();
            foreach (var root in request.FileRoots)
            {
                var storageFile = StorageFileFor(root.Name);
                StorageArchiveResult archived = new(0, 0);
                var storageEntry = await WriteFileAsync(partial, storageFile, written, progress, async target =>
                    archived = await ArchiveAsync(root.Path, target, request.PublicKey, cancellationToken).ConfigureAwait(false),
                    cancellationToken).ConfigureAwait(false);
                written += storageEntry.Bytes;
                files.Add(storageEntry);
                storages.Add(new BackupStorageEntry(root.Name, root.Path, archived.Files, archived.Bytes, storageFile));
            }

            var manifest = new BackupManifest(
                BackupManifest.CurrentFormat,
                request.SystemVersion,
                request.CreatedAt,
                request.CreatedByUserId,
                BackupKeys.FingerprintHex(request.PublicKey),
                databases,
                storages,
                files);
            await manifest.SaveAsync(partial, cancellationToken).ConfigureAwait(false);

            Directory.Move(partial, final);
            return new BackupResult(folderName, manifest, written);
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
    }

    // Архив каталога пишется в канал, шифрование читает из него — одновременно, без промежуточного файла. Сбой одной
    // стороны останавливает другую: упаковка не повиснет на канале, который никто не читает.
    private static async Task<StorageArchiveResult> ArchiveAsync(string root, Stream target, RSA publicKey, CancellationToken cancellationToken)
    {
        var pipe = new Pipe();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pack = Task.Run(async () =>
        {
            Exception? failure = null;
            try
            {
                await using var writer = pipe.Writer.AsStream(leaveOpen: true);
                return await StorageArchive.WriteAsync(root, writer, stop.Token).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure = exception;
                throw;
            }
            finally
            {
                await pipe.Writer.CompleteAsync(failure).ConfigureAwait(false);
            }
        }, CancellationToken.None);
        try
        {
            await using var reader = pipe.Reader.AsStream();
            await BackupCipher.EncryptAsync(reader, target, publicKey, stop.Token).ConfigureAwait(false);
        }
        catch
        {
            await stop.CancelAsync().ConfigureAwait(false);
            try
            {
                await pack.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Причина — в исключении шифрования ниже; сбой упаковки здесь его следствие.
            }

            throw;
        }

        return await pack.ConfigureAwait(false);
    }

    private static async Task<BackupFileEntry> WriteFileAsync(
        string folder, string name, long before, Action<long>? progress, Func<Stream, Task> write, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(Path.Combine(folder, name), FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20);
        await using var hashing = new HashingWriteStream(file, progress is null ? null : bytes => progress(before + bytes));
        await write(hashing).ConfigureAwait(false);
        await hashing.FlushAsync(cancellationToken).ConfigureAwait(false);
        return new BackupFileEntry(name, hashing.Written, hashing.HashHex());
    }

    // Имя базы в имени файла: только буквы, цифры, «-» и «_» — без сюрпризов файловой системы носителя.
    private static string SafeName(string name) =>
        new([.. name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_')]);

    private static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Недописанный каталог с окончанием .partial за копию не сойдёт и без удаления.
        }
    }
}
