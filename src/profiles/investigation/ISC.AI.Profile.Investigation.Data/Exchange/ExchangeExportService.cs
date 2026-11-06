using System.Globalization;
using System.Security.Cryptography;
using System.Threading.Channels;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Exchange;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Domain.Entities;
using ISC.AI.Profile.Investigation.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ISC.AI.Profile.Investigation.Data.Exchange;

/// <summary>
/// Задание на формирование пакета. Пароль ключа подписи — массивом, а не строкой: после подписи манифеста он затирается
/// в памяти (ТБ-083); в базу, журнал и логи он не попадает.
/// </summary>
/// <param name="ExportId">Строка журнала выгрузок.</param>
/// <param name="Request">Отбор.</param>
/// <param name="UserId">Кто запустил.</param>
/// <param name="Password">Пароль ключа подписи.</param>
public sealed record ExchangeExportJob(int ExportId, ExchangeExportRequest Request, int UserId, char[] Password);

/// <summary>Очередь заданий обмена: одна на процесс, читает её <see cref="ExchangeExportWorker"/>.</summary>
public sealed class ExchangeExportChannel
{
    private readonly Channel<ExchangeExportJob> _channel =
        Channel.CreateUnbounded<ExchangeExportJob>(new UnboundedChannelOptions { SingleReader = true });

    /// <summary>Сколько записано текущей выгрузкой (для экрана; в базу — реже).</summary>
    public long RunningBytes { get; set; }

    /// <summary>Ставит задание.</summary>
    public ValueTask WriteAsync(ExchangeExportJob job, CancellationToken cancellationToken = default) =>
        _channel.Writer.WriteAsync(job, cancellationToken);

    /// <summary>Читает задания до остановки хоста.</summary>
    public IAsyncEnumerable<ExchangeExportJob> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}

/// <summary>
/// Обмен между офисами — сторона источника (ADR-0068): готовность, журнал выгрузок, запуск и выполнение фонового задания,
/// акт в журнале аудита, уборка после перезапуска хоста.
/// </summary>
public sealed partial class ExchangeExportService(
    IDbContextFactory<InvestigationDbContext> contextFactory,
    ExchangeSettings settings,
    ExchangeExportChannel channel,
    ExchangePackageWriter writer,
    IExchangePackages packages,
    IAuditWriter audit,
    TimeProvider timeProvider,
    ILogger<ExchangeExportService> logger) : IExchangeExport
{
    // Журнал выгрузок на экране — последние.
    private const int RecentLimit = 20;

    /// <inheritdoc />
    public async Task<ExchangeOverview> GetOverviewAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var recent = await db.ExchangeExports.AsNoTracking()
            .OrderByDescending(e => e.StartedAt)
            .Take(RecentLimit)
            .Select(e => new ExchangeExportRow(
                e.Id, e.StartedAt, e.FinishedAt, e.Status, e.StartedByUserId, e.Year, e.Month, e.CaseNumbers, e.MediaKinds,
                e.PackageName, e.Cases, e.Persons, e.Links, e.Reports, e.Assets, e.Files, e.Appearances, e.BytesWritten,
                e.Classification, e.Error))
            .ToListAsync(cancellationToken);
        var running = recent.Any(r => r.Status == ExchangeExportStatus.Running) ? channel.RunningBytes : (long?)null;
        return new ExchangeOverview(settings.Check(packages), recent, running);
    }

    /// <inheritdoc />
    public async Task<ExchangeStartResult> StartAsync(
        ExchangeExportRequest request, int userId, string signingKeyPassword, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(signingKeyPassword);

        var readiness = settings.Check(packages);
        if (!readiness.Ready)
        {
            return new ExchangeStartResult(null, "Обмен не настроен: " + string.Join(" ", readiness.Problems));
        }

        // Пароль — проверяем сразу: неверный пароль виден на экране, а не через минуту в журнале выгрузок.
        try
        {
            using var key = settings.LoadSigningKey(packages, signingKeyPassword);
        }
        catch (InvalidOperationException exception)
        {
            return new ExchangeStartResult(null, exception.Message);
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (await db.ExchangeExports.AnyAsync(e => e.Status == ExchangeExportStatus.Running, cancellationToken))
        {
            return new ExchangeStartResult(null, "Пакет уже формируется — дождитесь окончания.");
        }

        var export = new ExchangeExport
        {
            StartedAt = timeProvider.GetUtcNow().UtcDateTime,
            Status = ExchangeExportStatus.Running,
            StartedByUserId = userId,
            Year = request.Year,
            Month = request.Month,
            CaseNumbers = request.CaseNumbers.Count == 0 ? null : string.Join(", ", request.CaseNumbers),
            MediaKinds = string.Join(",", request.MediaKinds),
            ResendFiles = request.ResendFiles,
        };
        db.ExchangeExports.Add(export);
        await db.SaveChangesAsync(cancellationToken);

        channel.RunningBytes = 0;
        await channel.WriteAsync(new ExchangeExportJob(export.Id, request, userId, signingKeyPassword.ToCharArray()), cancellationToken);
        return new ExchangeStartResult(export.Id, null);
    }

    /// <summary>
    /// Выполняет задание: ключи, сборка пакета, журнал выгрузок, акт в журнале аудита. Пароль затирается в любом исходе.
    /// </summary>
    public async Task RunAsync(ExchangeExportJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        try
        {
            var createdAt = timeProvider.GetUtcNow();
            var (from, to) = ExchangePackageJob.MonthUtc(job.Request.Year, job.Request.Month, timeProvider.LocalTimeZone);
            using var recipientKey = settings.LoadRecipientKey(packages);
            using var signingKey = settings.LoadSigningKey(packages, job.Password);
            CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(job.Password.AsSpan()));

            // Ход — в памяти процесса: экран читает его из очереди, база получает итог одной записью.
            var result = await writer.WriteAsync(
                new ExchangePackageJob(job.Request, from, to, job.UserId, createdAt),
                settings.InstanceId!,
                settings.TargetFolder!,
                recipientKey,
                signingKey,
                bytes => channel.RunningBytes = bytes,
                cancellationToken);

            await FinishAsync(job.ExportId, result, cancellationToken);
            await WriteActAsync(job, result, cancellationToken);
            LogExported(logger, job.ExportId, result.FolderName);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogExportFailed(logger, exception, job.ExportId);
            await FailAsync(job.ExportId, exception, CancellationToken.None);
            await WriteFailureAsync(job, exception, CancellationToken.None);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(job.Password.AsSpan()));
            channel.RunningBytes = 0;
        }
    }

    /// <summary>
    /// После перезапуска хоста: незавершённые выгрузки — «прервана», их каталоги «.partial» на носителе удаляются (пакета
    /// нет — пусть никто не примет обрывок за пакет).
    /// </summary>
    public async Task<int> RecoverAfterRestartAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var interrupted = await db.ExchangeExports.Where(e => e.Status == ExchangeExportStatus.Running).ExecuteUpdateAsync(s => s
            .SetProperty(e => e.Status, ExchangeExportStatus.Interrupted)
            .SetProperty(e => e.FinishedAt, now)
            .SetProperty(e => e.Error, "Хост перезапущен посреди формирования — пакета нет, сформируйте заново."), cancellationToken);

        if (settings.TargetFolder is { } target)
        {
            packages.RemoveUnfinished(target, ExchangePackageWriter.FolderPrefix);
        }

        return interrupted;
    }

    private async Task FinishAsync(int exportId, ExchangePackageResult result, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var export = await db.ExchangeExports.SingleAsync(e => e.Id == exportId, cancellationToken);
        var counts = result.Content.Counts;
        export.Status = ExchangeExportStatus.Succeeded;
        export.FinishedAt = timeProvider.GetUtcNow().UtcDateTime;
        export.PackageName = result.FolderName;
        export.Cases = counts.GetValueOrDefault("cases");
        export.Persons = counts.GetValueOrDefault("persons");
        export.Links = counts.GetValueOrDefault("links");
        export.Reports = counts.GetValueOrDefault("reports");
        export.Assets = counts.GetValueOrDefault("assets");
        export.Files = counts.GetValueOrDefault("files");
        export.Appearances = counts.GetValueOrDefault("appearances");
        export.BytesWritten = result.Package.Bytes;
        export.Classification = result.Content.Classification;

        // Ушедшие файлы — чтобы следующий пакет их не повторял (ADR-0068).
        db.ExchangeExportFiles.AddRange(result.ShippedAssetIds.Select(id => new ExchangeExportFile { ExportId = exportId, AssetId = id }));
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task FailAsync(int exportId, Exception exception, CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            await db.ExchangeExports.Where(e => e.Id == exportId).ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Status, ExchangeExportStatus.Failed)
                .SetProperty(e => e.FinishedAt, timeProvider.GetUtcNow().UtcDateTime)
                .SetProperty(e => e.Error, Truncate(exception.Message, 2000)), cancellationToken);
        }
        catch (Exception failure) when (failure is DbUpdateException or InvalidOperationException or IOException)
        {
            LogFailMarkFailed(logger, failure, exportId);
        }
    }

    // Акт экспорта (ТФ-ОБМ-01): гриф — маркировка пакета; счётчики, отпечатки ключей и SHA-256 манифеста — чтобы акт
    // однозначно указывал на пакет, ушедший на носителе.
    private Task WriteActAsync(ExchangeExportJob job, ExchangePackageResult result, CancellationToken cancellationToken)
    {
        var (content, package) = (result.Content, result.Package);
        var counts = string.Join(", ", content.Counts.Select(c => c.Key + "=" + c.Value.ToString(CultureInfo.InvariantCulture)));
        return audit.WriteAsync(
            new AuditEntry(
                AuditAction.Export,
                content.Classification,
                SubjectId: job.UserId,
                ObjectRef: "investigation:exchange:package:" + package.FolderName,
                PayloadSensitive: $"пакет обмена {package.FolderName}: период {content.Period}, отбор: "
                    + string.Join("; ", content.Selection.Select(s => s.Key + "=" + s.Value)) + $"; {counts}; "
                    + $"объём {package.Bytes.ToString(CultureInfo.InvariantCulture)} байт; маркировка «{content.Marking}»; "
                    + $"ключ получателя {package.RecipientKeyFingerprint}; ключ подписи {package.SigningKeyFingerprint}; "
                    + $"SHA-256 манифеста {package.ManifestSha256}"),
            cancellationToken);
    }

    private async Task WriteFailureAsync(ExchangeExportJob job, Exception exception, CancellationToken cancellationToken)
    {
        try
        {
            await audit.WriteAsync(
                new AuditEntry(
                    AuditAction.Export,
                    ClassificationLevels.Unclassified,
                    SubjectId: job.UserId,
                    ObjectRef: "investigation:exchange:export:" + job.ExportId.ToString(CultureInfo.InvariantCulture),
                    PayloadSensitive: "пакет обмена не сформирован: " + Truncate(exception.Message, 500),
                    Outcome: AuditOutcome.Error),
                cancellationToken);
        }
        catch (Exception failure) when (failure is DbUpdateException or InvalidOperationException or IOException)
        {
            LogFailMarkFailed(logger, failure, job.ExportId);
        }
    }

    private static string Truncate(string text, int length) => text.Length <= length ? text : text[..length];

    [LoggerMessage(EventId = 6801, Level = LogLevel.Information, Message = "Пакет обмена сформирован: выгрузка {ExportId}, {FolderName}")]
    private static partial void LogExported(ILogger logger, int exportId, string folderName);

    [LoggerMessage(EventId = 6802, Level = LogLevel.Error, Message = "Пакет обмена не сформирован: выгрузка {ExportId}")]
    private static partial void LogExportFailed(ILogger logger, Exception exception, int exportId);

    [LoggerMessage(EventId = 6803, Level = LogLevel.Error, Message = "Не удалось отметить сбой выгрузки {ExportId}")]
    private static partial void LogFailMarkFailed(ILogger logger, Exception exception, int exportId);
}

/// <summary>
/// Исполнитель заданий обмена (ADR-0068): при старте — уборка прерванных выгрузок, дальше — задания по одному, каждое в
/// своей области служб. Сбой задания исполнитель не роняет.
/// </summary>
public sealed partial class ExchangeExportWorker(
    ExchangeExportChannel channel, IServiceScopeFactory scopes, ILogger<ExchangeExportWorker> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // База может быть недоступна на старте (хост поднимается и без неё) — не роняем исполнитель, только журнал.
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var interrupted = await scope.ServiceProvider.GetRequiredService<ExchangeExportService>().RecoverAfterRestartAsync(stoppingToken);
            if (interrupted > 0)
            {
                LogRecovered(logger, interrupted);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogRecoverFailed(logger, exception);
        }

        try
        {
            await foreach (var job in channel.ReadAllAsync(stoppingToken))
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<ExchangeExportService>().RunAsync(job, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Остановка хоста: незавершённая выгрузка будет помечена «прервана» при следующем старте.
        }
    }

    [LoggerMessage(EventId = 6804, Level = LogLevel.Warning, Message = "Прерванные выгрузки пакетов обмена помечены: {Count}")]
    private static partial void LogRecovered(ILogger logger, int count);

    [LoggerMessage(EventId = 6805, Level = LogLevel.Error, Message = "Уборка прерванных выгрузок пакетов обмена не выполнена")]
    private static partial void LogRecoverFailed(ILogger logger, Exception exception);
}
