using System.Globalization;
using System.IO.Pipelines;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using Npgsql;

namespace ISC.AI.Backup;

/// <summary>
/// Отметка о восстановлении (ТБ-082, ADR-0063): пишется в восстановленное хранилище, хост при первом запуске переносит её
/// в журнал аудита — восстановленная база не может содержать записи о самом восстановлении.
/// </summary>
/// <param name="RestoredAt">Когда восстановлено.</param>
/// <param name="BackupFolder">Из какой копии (имя каталога).</param>
/// <param name="BackupCreatedAt">Когда сделана копия.</param>
/// <param name="Administrator">ФИО Администратора.</param>
/// <param name="KeyCustodian">ФИО хранителя ключа.</param>
/// <param name="Databases">Восстановленные базы.</param>
/// <param name="PreviousDatabases">Под какими именами сохранены прежние базы.</param>
/// <param name="PreviousStorages">Куда сохранены прежние каталоги файлов (каталога не было — его нет и в списке).</param>
public sealed record RestoreMarker(
    DateTimeOffset RestoredAt,
    string BackupFolder,
    DateTimeOffset BackupCreatedAt,
    string Administrator,
    string KeyCustodian,
    IReadOnlyList<string> Databases,
    IReadOnlyList<string> PreviousDatabases,
    IReadOnlyList<string> PreviousStorages)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Пишет отметку в каталог отметок хранилища.</summary>
    public async Task SaveAsync(string storageRoot, CancellationToken cancellationToken = default)
    {
        var folder = Path.Combine(storageRoot, StorageArchive.RestoreJournalFolder);
        Directory.CreateDirectory(folder);
        var name = "restore-" + RestoredAt.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".json";
        await using var stream = File.Create(Path.Combine(folder, name));
        await JsonSerializer.SerializeAsync(stream, this, Json, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Отметки, ещё не перенесённые в журнал (файлы в корне каталога отметок).</summary>
    public static IReadOnlyList<string> Pending(string storageRoot)
    {
        var folder = Path.Combine(storageRoot, StorageArchive.RestoreJournalFolder);
        return Directory.Exists(folder) ? Directory.GetFiles(folder, "restore-*.json").Order(StringComparer.Ordinal).ToList() : [];
    }

    /// <summary>Читает отметку.</summary>
    public static async Task<RestoreMarker> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<RestoreMarker>(stream, Json, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException($"Отметка о восстановлении пуста: {path}");
    }
}

/// <summary>
/// Восстановление из копии (ТБ-082, ADR-0063) — для программы восстановления, при остановленном хосте. Ничего не
/// уничтожается: база восстанавливается в новую (<c>&lt;имя&gt;_restore_&lt;время&gt;</c>), затем прежняя
/// переименовывается в <c>&lt;имя&gt;_before_restore_&lt;время&gt;</c>, восстановленная — в рабочее имя; хранилище —
/// распаковкой рядом и переименованием каталогов. Прежнее удаляет Администратор после проверки.
/// </summary>
public sealed class BackupRestorer(PgTools tools)
{
    /// <summary>
    /// Сколько других клиентских подключений к базе (хост должен быть остановлен — иначе восстановление «на живую»).
    /// Служебные процессы сервера (autovacuum и т. п.) не считаются: переименование базы их само завершает.
    /// </summary>
    public static async Task<int> OtherConnectionsAsync(NpgsqlConnectionStringBuilder server, string database, CancellationToken cancellationToken = default)
    {
        await using var db = await OpenMaintenanceAsync(server, database, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM pg_stat_activity WHERE datname = @db AND pid <> pg_backend_pid() AND backend_type = 'client backend'", db);
        command.Parameters.AddWithValue("db", database);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Восстанавливает базу из зашифрованного файла копии в новую базу <paramref name="newName"/> (создаётся). Рабочую базу
    /// не трогает — подмена отдельным шагом <see cref="SwapDatabaseAsync"/>.
    /// </summary>
    public async Task RestoreDatabaseAsync(
        NpgsqlConnectionStringBuilder server, string encryptedFile, RSA privateKey, string newName, CancellationToken cancellationToken = default)
    {
        await ExecuteAsync(server, newName, $"CREATE DATABASE {Quote(newName)} TEMPLATE template0 ENCODING 'UTF8'", cancellationToken).ConfigureAwait(false);
        await tools.RestoreAsync(server, newName, input => DecryptIntoAsync(encryptedFile, privateKey, input, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Подменяет рабочую базу восстановленной: рабочая (если есть) — в <paramref name="previousName"/>, восстановленная — в
    /// рабочее имя. Оба переименования — одной транзакцией: не вышло второе — первое не состоялось. Возвращает, была ли
    /// рабочая база.
    /// </summary>
    public static async Task<bool> SwapDatabaseAsync(
        NpgsqlConnectionStringBuilder server, string database, string restoredName, string previousName, CancellationToken cancellationToken = default)
    {
        await using var db = await OpenMaintenanceAsync(server, database, cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var exists = new NpgsqlCommand("SELECT count(*) FROM pg_database WHERE datname = @db", db, transaction);
        exists.Parameters.AddWithValue("db", database);
        var had = Convert.ToInt32(await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) > 0;
        if (had)
        {
            await using var rename = new NpgsqlCommand($"ALTER DATABASE {Quote(database)} RENAME TO {Quote(previousName)}", db, transaction);
            await rename.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var promote = new NpgsqlCommand($"ALTER DATABASE {Quote(restoredName)} RENAME TO {Quote(database)}", db, transaction);
        await promote.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return had;
    }

    /// <summary>
    /// Отменяет <see cref="SwapDatabaseAsync"/>: восстановленная — обратно в <paramref name="restoredName"/>, прежняя (если
    /// была) — в рабочее имя. Одной транзакцией.
    /// </summary>
    public static async Task UndoSwapDatabaseAsync(
        NpgsqlConnectionStringBuilder server, string database, string restoredName, string? previousName, CancellationToken cancellationToken = default)
    {
        await using var db = await OpenMaintenanceAsync(server, database, cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var demote = new NpgsqlCommand($"ALTER DATABASE {Quote(database)} RENAME TO {Quote(restoredName)}", db, transaction);
        await demote.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (previousName is not null)
        {
            await using var back = new NpgsqlCommand($"ALTER DATABASE {Quote(previousName)} RENAME TO {Quote(database)}", db, transaction);
            await back.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Распаковывает хранилище из зашифрованного файла копии в каталог <paramref name="destination"/>.</summary>
    public static async Task<StorageArchiveResult> RestoreStorageAsync(
        string encryptedFile, RSA privateKey, string destination, CancellationToken cancellationToken = default)
    {
        var pipe = new Pipe();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var decrypt = Task.Run(async () =>
        {
            Exception? failure = null;
            try
            {
                await using var writer = pipe.Writer.AsStream(leaveOpen: true);
                await DecryptIntoAsync(encryptedFile, privateKey, writer, stop.Token).ConfigureAwait(false);
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

        StorageArchiveResult result;
        try
        {
            await using var reader = pipe.Reader.AsStream();
            result = await StorageArchive.ExtractAsync(reader, destination, stop.Token).ConfigureAwait(false);
        }
        catch
        {
            await stop.CancelAsync().ConfigureAwait(false);
            try
            {
                await decrypt.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Причина — в исключении распаковки; оно уходит наверх.
            }

            throw;
        }

        await decrypt.ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Подменяет каталог файлов распакованным; прежний (если есть) — в <paramref name="previous"/>. Не вышло второе
    /// переименование — прежний возвращается на место (рабочий каталог не пропадает). Возвращает, был ли прежний.
    /// </summary>
    public static bool SwapStorage(string storageRoot, string restored, string previous)
    {
        var had = Directory.Exists(storageRoot);
        if (had)
        {
            Directory.Move(storageRoot, previous);
        }

        try
        {
            Directory.Move(restored, storageRoot);
        }
        catch when (had)
        {
            Directory.Move(previous, storageRoot);
            throw;
        }

        return had;
    }

    /// <summary>Отменяет <see cref="SwapStorage"/>: восстановленный — обратно в <paramref name="restored"/>, прежний — на место.</summary>
    public static void UndoSwapStorage(string storageRoot, string restored, string? previous)
    {
        Directory.Move(storageRoot, restored);
        if (previous is not null)
        {
            Directory.Move(previous, storageRoot);
        }
    }

    private static async Task DecryptIntoAsync(string encryptedFile, RSA privateKey, Stream output, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(encryptedFile, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        await BackupCipher.DecryptAsync(input, output, privateKey, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExecuteAsync(NpgsqlConnectionStringBuilder server, string database, string sql, CancellationToken cancellationToken)
    {
        await using var db = await OpenMaintenanceAsync(server, database, cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, db);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Подключение к служебной базе сервера для работы с базой <paramref name="database"/>: «postgres» (рабочую базу
    /// переименовывают, к ней подключаться нельзя); если рабочая сама называется «postgres» — «template1». Без пула.
    /// </summary>
    public static NpgsqlConnectionStringBuilder Maintenance(NpgsqlConnectionStringBuilder server, string database)
    {
        ArgumentNullException.ThrowIfNull(server);
        var maintenance = string.Equals(database, "postgres", StringComparison.Ordinal) ? "template1" : "postgres";
        return new NpgsqlConnectionStringBuilder(server.ConnectionString) { Database = maintenance, Pooling = false };
    }

    private static async Task<NpgsqlConnection> OpenMaintenanceAsync(
        NpgsqlConnectionStringBuilder server, string database, CancellationToken cancellationToken)
    {
        var db = new NpgsqlConnection(Maintenance(server, database).ConnectionString);
        await db.OpenAsync(cancellationToken).ConfigureAwait(false);
        return db;
    }

    // Имя базы в DDL — в двойных кавычках с удвоением кавычек внутри (имя приходит из манифеста и конфигурации).
    private static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
