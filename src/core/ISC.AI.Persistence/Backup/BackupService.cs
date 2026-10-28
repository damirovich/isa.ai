using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Backup;
using ISC.AI.Backup;
using ISC.AI.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace ISC.AI.Persistence.Backup;

/// <summary>
/// Резервные копии (ТФ-АДМ-04, ТБ-082, ADR-0063): состояние для экрана, постановка копии и проверки в отдельную очередь
/// (<see cref="BackupJobChannel"/>), выполнение — <see cref="RunCopyAsync"/> и <see cref="RunVerifyAsync"/> (их вызывает
/// исполнитель хоста), восстановление после перезапуска и перенос отметок о восстановлении в журнал аудита.
/// </summary>
/// <remarks>
/// ИНВАРИАНТЫ: ключ используется только с совпавшим пином (ТИ-004) — подменённый ключ копию не получит; в журнал аудита
/// уходят номер прогона, каталог и объёмы — без путей ключей и без содержимого (ТБ-030/032); копия пишется не больше одной
/// за раз (уникальный индекс <c>ux_backup_run_single_running</c>).
/// </remarks>
public sealed partial class BackupService(
    IDbContextFactory<CoreDbContext> contexts,
    BackupSettings settings,
    BackupJobChannel jobs,
    IAuditWriter audit,
    TimeProvider time,
    ILogger<BackupService> logger) : IBackupService
{
    /// <summary>Сколько последних прогонов показывает экран.</summary>
    public const int RunsShown = 20;

    // Объём хранилища считается обходом файлов — дорого на терабайтах; экран спрашивает его часто, поэтому — кеш.
    private static readonly TimeSpan EstimateLifetime = TimeSpan.FromMinutes(10);
    private static readonly Lock EstimateGate = new();
    private static (string Roots, DateTimeOffset At, long Bytes)? _storageEstimate;

    // Клиент PostgreSQL под версию сервера проверяется обращением к серверу и запуском программ — тоже кеш, но короткий:
    // поставили нужный клиент — экран увидит это через минуту, без перезапуска.
    private static readonly TimeSpan ClientCheckLifetime = TimeSpan.FromMinutes(1);
    private static (string Key, DateTimeOffset At, IReadOnlyList<string> Problems)? _clientCheck;

    /// <inheritdoc />
    public async Task<BackupOverview> GetOverviewAsync(CancellationToken cancellationToken = default)
    {
        var problems = await ProblemsAsync(cancellationToken);
        var available = settings.TargetFolder is { } target && Directory.Exists(target);
        long? free = null;
        if (available)
        {
            try
            {
                free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(settings.TargetFolder!))!).AvailableFreeSpace;
            }
            catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException)
            {
                free = null; // сетевой путь без буквы диска — свободное место не узнать
            }
        }

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var runs = await db.BackupRuns.AsNoTracking()
            .OrderByDescending(r => r.StartedAt)
            .Take(RunsShown)
            .Select(r => new BackupRunRow(
                r.Id, r.StartedAt, r.FinishedAt, r.Status, r.StartedByUserId, r.FolderName, r.Databases, r.StorageFiles, r.StorageBytes,
                r.BytesWritten, r.Error, r.VerifyStatus, r.VerifiedAt, r.VerifyNote))
            .ToListAsync(cancellationToken);

        return new BackupOverview(
            problems, settings.TargetFolder, available, free, await EstimateAsync(cancellationToken), settings.PublicKeySha256,
            settings.ReminderDays, runs);
    }

    /// <inheritdoc />
    public async Task<BackupActionResult> StartAsync(int startedByUserId, CancellationToken cancellationToken = default)
    {
        var problems = await ProblemsAsync(cancellationToken);
        if (problems.Count > 0)
        {
            return new BackupActionResult(null, string.Join(" ", problems));
        }

        if (!Directory.Exists(settings.TargetFolder))
        {
            return new BackupActionResult(null, $"Носитель не подключён: каталог {settings.TargetFolder} недоступен.");
        }

        var now = time.GetUtcNow();
        var run = new BackupRunEntity
        {
            StartedAt = now.UtcDateTime,
            Status = BackupRunStatus.Running,
            StartedByUserId = startedByUserId,
            FolderName = BackupWriter.FolderNameFor(now.ToLocalTime()),
            Databases = settings.Databases.Count,
        };

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        db.BackupRuns.Add(run);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return new BackupActionResult(null, "Копия уже делается — дождитесь её окончания.");
        }

        await jobs.WriteAsync(new BackupJob(run.Id, BackupJobKind.Copy, startedByUserId), cancellationToken);
        return new BackupActionResult(run.Id, null);
    }

    /// <inheritdoc />
    public async Task<BackupActionResult> VerifyLatestAsync(int verifiedByUserId, CancellationToken cancellationToken = default)
    {
        if (settings.TargetFolder is not { } target || !Directory.Exists(target))
        {
            return new BackupActionResult(null, $"Носитель не подключён: каталог {settings.TargetFolder ?? "(не задан)"} недоступен.");
        }

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var candidates = await db.BackupRuns
            .Where(r => r.Status == BackupRunStatus.Succeeded && r.FolderName != null)
            .OrderByDescending(r => r.StartedAt)
            .Take(RunsShown)
            .ToListAsync(cancellationToken);
        var run = candidates.FirstOrDefault(r => Directory.Exists(Path.Combine(target, r.FolderName!)));
        if (run is null)
        {
            return new BackupActionResult(null, "На подключённом носителе нет готовой копии из журнала.");
        }

        if (run.VerifyStatus == BackupVerifyStatus.Running)
        {
            return new BackupActionResult(null, "Проверка этой копии уже идёт.");
        }

        run.VerifyStatus = BackupVerifyStatus.Running;
        run.VerifiedByUserId = verifiedByUserId;
        run.VerifyNote = null;
        await db.SaveChangesAsync(cancellationToken);
        await jobs.WriteAsync(new BackupJob(run.Id, BackupJobKind.Verify, verifiedByUserId), cancellationToken);
        return new BackupActionResult(run.Id, null);
    }

    /// <summary>
    /// Делает копию прогона <paramref name="runId"/>: базы, хранилище, манифест; ход — в строке прогона раз в несколько
    /// секунд; итог — в строке и в журнале аудита (<see cref="AuditAction.Export"/>: данные уходят на носитель).
    /// </summary>
    public async Task RunCopyAsync(int runId, int userId, CancellationToken cancellationToken)
    {
        var written = 0L;
        using var progressStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var progressLoop = Task.Run(() => SaveProgressLoopAsync(runId, () => Interlocked.Read(ref written), progressStop.Token), CancellationToken.None);
        try
        {
            using var key = LoadPinnedKey();

            // Время копии — время постановки: имя каталога то же, что экран показал при запуске.
            await using var db = await contexts.CreateDbContextAsync(cancellationToken);
            var startedAt = await db.BackupRuns.Where(r => r.Id == runId).Select(r => r.StartedAt).SingleAsync(cancellationToken);
            var createdAt = new DateTimeOffset(DateTime.SpecifyKind(startedAt, DateTimeKind.Utc)).ToLocalTime();
            var result = await new BackupWriter(new PgTools(settings.PgDumpPath)).WriteAsync(
                new BackupRequest(settings.TargetFolder!, settings.Databases, settings.FileRoots, key, SystemVersion(), userId, createdAt),
                bytes => Interlocked.Exchange(ref written, bytes),
                cancellationToken);

            // Копия на носителе — итог записывается и при остановке хоста: иначе готовая копия числилась бы прерванной.
            await StopAsync(progressStop, progressLoop);
            var storages = result.Manifest.Storages;
            await UpdateRunAsync(runId, run =>
            {
                run.Status = BackupRunStatus.Succeeded;
                run.FinishedAt = time.GetUtcNow().UtcDateTime;
                run.FolderName = result.FolderName;
                run.Databases = result.Manifest.Databases.Count;
                run.StorageFiles = storages.Sum(s => s.Files);
                run.StorageBytes = storages.Sum(s => s.Bytes);
                run.BytesWritten = result.BytesWritten;
            }, CancellationToken.None);
            var byRoot = string.Join(", ", storages.Select(s => s.Name + " — " + s.Files.ToString(CultureInfo.InvariantCulture)));
            await WriteAuditAsync(AuditAction.Export, userId, $"core:backup:{runId}", string.Create(CultureInfo.InvariantCulture,
                $"резервная копия {result.FolderName}: баз {result.Manifest.Databases.Count}, файлов {storages.Sum(s => s.Files)} "
                + $"({byRoot}), на носитель {result.BytesWritten} байт; ключ {result.Manifest.KeyFingerprint[..16]}…"), CancellationToken.None);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await StopAsync(progressStop, progressLoop);
            await UpdateRunAsync(runId, run =>
            {
                run.Status = BackupRunStatus.Interrupted;
                run.FinishedAt = time.GetUtcNow().UtcDateTime;
                run.Error = "Прервана остановкой системы — сделайте копию заново.";
            }, CancellationToken.None);
        }
        catch (Exception exception)
        {
            await StopAsync(progressStop, progressLoop);
            LogCopyFailed(logger, exception, runId);
            var reason = Trim(exception.Message);
            await UpdateRunAsync(runId, run =>
            {
                run.Status = BackupRunStatus.Failed;
                run.FinishedAt = time.GetUtcNow().UtcDateTime;
                run.Error = reason;
            }, CancellationToken.None);
            await WriteAuditAsync(AuditAction.Export, userId, $"core:backup:{runId}", "резервная копия не удалась: " + reason, CancellationToken.None);
        }
    }

    /// <summary>Проверяет манифест копии прогона на носителе; итог — в строке прогона и в журнале аудита.</summary>
    public async Task RunVerifyAsync(int runId, int userId, CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await contexts.CreateDbContextAsync(cancellationToken);
            var folderName = await db.BackupRuns.Where(r => r.Id == runId).Select(r => r.FolderName).SingleAsync(cancellationToken);
            var check = await BackupVerifier.VerifyAsync(Path.Combine(settings.TargetFolder!, folderName!), settings.PublicKeySha256, cancellationToken);
            await UpdateRunAsync(runId, run =>
            {
                run.VerifyStatus = check.Ok ? BackupVerifyStatus.Ok : BackupVerifyStatus.Problems;
                run.VerifiedAt = time.GetUtcNow().UtcDateTime;
                run.VerifyNote = check.Ok ? null : Trim(string.Join(" ", check.Problems));
            }, CancellationToken.None);
            await WriteAuditAsync(AuditAction.View, userId, $"core:backup:{runId}:verify",
                check.Ok ? $"проверка манифеста копии {folderName}: цела" : $"проверка манифеста копии {folderName}: расхождений {check.Problems.Count}",
                CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogVerifyFailed(logger, exception, runId);
            await UpdateRunAsync(runId, run =>
            {
                run.VerifyStatus = BackupVerifyStatus.Problems;
                run.VerifiedAt = time.GetUtcNow().UtcDateTime;
                run.VerifyNote = Trim("Проверка не выполнена: " + exception.Message);
            }, CancellationToken.None);
        }
    }

    /// <summary>
    /// После перезапуска хоста: прогоны «идёт» — «прервана», проверки «идёт» — с пояснением. Задания жили только в памяти.
    /// </summary>
    public async Task<int> RecoverAfterRestartAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var now = time.GetUtcNow().UtcDateTime;
        var copies = await db.BackupRuns.Where(r => r.Status == BackupRunStatus.Running).ExecuteUpdateAsync(s => s
            .SetProperty(r => r.Status, BackupRunStatus.Interrupted)
            .SetProperty(r => r.FinishedAt, now)
            .SetProperty(r => r.Error, "Прервана перезапуском системы — сделайте копию заново."), cancellationToken);
        var checks = await db.BackupRuns.Where(r => r.VerifyStatus == BackupVerifyStatus.Running).ExecuteUpdateAsync(s => s
            .SetProperty(r => r.VerifyStatus, BackupVerifyStatus.Problems)
            .SetProperty(r => r.VerifiedAt, now)
            .SetProperty(r => r.VerifyNote, "Проверка прервана перезапуском системы — проверьте заново."), cancellationToken);
        return copies + checks;
    }

    /// <summary>
    /// Отметки о восстановлении (их пишет программа восстановления в хранилище) — в журнал аудита поимённо, затем в
    /// подкаталог <c>done</c>: восстановленная база не содержит записи о самом восстановлении (ТБ-082).
    /// </summary>
    public async Task<int> ProcessRestoreMarkersAsync(CancellationToken cancellationToken = default)
    {
        var processed = 0;
        foreach (var path in RestoreMarker.Pending(settings.StorageRoot))
        {
            var marker = await RestoreMarker.LoadAsync(path, cancellationToken);
            var previousFiles = marker.PreviousStorages.Count > 0
                ? "; каталоги файлов — " + string.Join(", ", marker.PreviousStorages.Select(Path.GetFileName))
                : string.Empty;
            await WriteAuditAsync(AuditAction.Modify, null, "core:backup:restore", string.Create(CultureInfo.InvariantCulture,
                $"восстановление из копии {marker.BackupFolder} (сделана {marker.BackupCreatedAt:dd.MM.yyyy HH:mm}) {marker.RestoredAt:dd.MM.yyyy HH:mm}: "
                + $"Администратор — {marker.Administrator}, хранитель ключа — {marker.KeyCustodian}; базы: {string.Join(", ", marker.Databases)}; "
                + $"прежние сохранены как: {string.Join(", ", marker.PreviousDatabases)}{previousFiles}"),
                cancellationToken);

            var done = Path.Combine(Path.GetDirectoryName(path)!, "done");
            Directory.CreateDirectory(done);
            File.Move(path, Path.Combine(done, Path.GetFileName(path)), overwrite: true);
            processed++;
        }

        return processed;
    }

    private async Task<List<string>> ProblemsAsync(CancellationToken cancellationToken)
    {
        var problems = settings.Problems().ToList();
        if (settings.PublicKeyPath is { } path && File.Exists(path) && settings.PublicKeySha256 is { } pin)
        {
            try
            {
                using var key = BackupKeys.LoadPublicKey(path);
                if (!string.Equals(BackupKeys.FingerprintHex(key), pin, StringComparison.OrdinalIgnoreCase))
                {
                    problems.Add("Отпечаток ключа не совпадает с пином Backup:PublicKey:Sha256 — ключ подменён или пин устарел (ТИ-004).");
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or IOException or CryptographicException)
            {
                problems.Add(exception.Message);
            }
        }

        try
        {
            _ = new PgTools(settings.PgDumpPath).DumpPath;
            problems.AddRange(await ClientProblemsAsync(cancellationToken));
        }
        catch (FileNotFoundException exception)
        {
            problems.Add(exception.Message);
        }

        return problems;
    }

    // pg_dump той же основной версии, что каждый сервер баз (ТБ-082: копия, снятая клиентом новее сервера, на него не
    // восстанавливается) — до нажатия «Сделать копию», а не ошибкой в журнале. Сервер недоступен — не про настройку:
    // это покажет сама копия.
    private async Task<IReadOnlyList<string>> ClientProblemsAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var key = settings.PgDumpPath + "|" + string.Join("|", settings.Databases.Select(d => d.Host + ":" + d.Port.ToString(CultureInfo.InvariantCulture)));
        lock (EstimateGate)
        {
            if (_clientCheck is { } cached && cached.Key == key && now - cached.At < ClientCheckLifetime)
            {
                return cached.Problems;
            }
        }

        var problems = new List<string>();
        var tools = new PgTools(settings.PgDumpPath);
        foreach (var server in settings.Databases.DistinctBy(d => (d.Host, d.Port)))
        {
            try
            {
                await tools.ForServerAsync("pg_dump", server, cancellationToken);
            }
            catch (Exception exception) when (exception is InvalidOperationException or FileNotFoundException)
            {
                problems.Add(exception.Message);
            }
            catch (NpgsqlException)
            {
                // Сервер недоступен — копия упадёт с понятной причиной; оценка объёма на экране тоже пуста.
            }
        }

        lock (EstimateGate)
        {
            _clientCheck = (key, now, problems);
        }

        return problems;
    }

    private RSA LoadPinnedKey()
    {
        var key = BackupKeys.LoadPublicKey(settings.PublicKeyPath!);
        if (!string.Equals(BackupKeys.FingerprintHex(key), settings.PublicKeySha256, StringComparison.OrdinalIgnoreCase))
        {
            key.Dispose();
            throw new InvalidOperationException("Отпечаток ключа не совпадает с пином Backup:PublicKey:Sha256 — копия не делается (ТИ-004).");
        }

        return key;
    }

    private async Task<long?> EstimateAsync(CancellationToken cancellationToken)
    {
        long total = 0;
        foreach (var database in settings.Databases)
        {
            try
            {
                total += await PgTools.DatabaseSizeAsync(database, cancellationToken);
            }
            catch (Exception exception) when (exception is NpgsqlException or InvalidOperationException)
            {
                return null;
            }
        }

        return total + StorageBytes();
    }

    private long StorageBytes()
    {
        var now = time.GetUtcNow();
        var roots = string.Join("|", settings.FileRoots.Select(r => r.Path));
        lock (EstimateGate)
        {
            if (_storageEstimate is { } cached && cached.Roots == roots && now - cached.At < EstimateLifetime)
            {
                return cached.Bytes;
            }
        }

        var bytes = 0L;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var root in settings.FileRoots.Where(r => Directory.Exists(r.Path)))
        {
            foreach (var file in new DirectoryInfo(root.Path).EnumerateFiles("*", options))
            {
                bytes += file.Length;
            }
        }

        lock (EstimateGate)
        {
            _storageEstimate = (roots, now, bytes);
        }

        return bytes;
    }

    private async Task SaveProgressLoopAsync(int runId, Func<long> written, CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
                var bytes = written();
                await using var db = await contexts.CreateDbContextAsync(cancellationToken);
                await db.BackupRuns.Where(r => r.Id == runId && r.Status == BackupRunStatus.Running)
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.BytesWritten, bytes), cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Копия закончилась — итог пишет вызывающий.
        }
        catch (Exception exception) when (exception is DbUpdateException or NpgsqlException)
        {
            LogProgressFailed(logger, exception, runId); // ход — подсказка экрану, его сбой копию не роняет
        }
    }

    private static async Task StopAsync(CancellationTokenSource stop, Task loop)
    {
        await stop.CancelAsync();
        await loop;
    }

    private async Task UpdateRunAsync(int runId, Action<BackupRunEntity> change, CancellationToken cancellationToken = default)
    {
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var run = await db.BackupRuns.SingleAsync(r => r.Id == runId, cancellationToken);
        change(run);
        await db.SaveChangesAsync(cancellationToken);
    }

    private Task WriteAuditAsync(AuditAction action, int? userId, string objectRef, string payload, CancellationToken cancellationToken = default) =>
        audit.WriteAsync(new AuditEntry(action, Classification: 0, userId, objectRef, PayloadSensitive: payload), cancellationToken);

    private static string SystemVersion() =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
        ?? "unknown";

    private static string Trim(string text) =>
        text.Length <= BackupRunEntity.MaxNoteLength ? text : text[..(BackupRunEntity.MaxNoteLength - 1)] + "…";

    [LoggerMessage(EventId = 6301, Level = LogLevel.Error, Message = "Резервная копия {RunId} не удалась")]
    private static partial void LogCopyFailed(ILogger logger, Exception exception, int runId);

    [LoggerMessage(EventId = 6302, Level = LogLevel.Error, Message = "Проверка резервной копии {RunId} не выполнена")]
    private static partial void LogVerifyFailed(ILogger logger, Exception exception, int runId);

    [LoggerMessage(EventId = 6303, Level = LogLevel.Warning, Message = "Не удалось записать ход резервной копии {RunId}")]
    private static partial void LogProgressFailed(ILogger logger, Exception exception, int runId);
}
