using System.Runtime.CompilerServices;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Data.Entities;
using ISC.AI.Modules.Media.Data.EntityConfigurations;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace ISC.AI.Modules.Media.Data;

/// <summary>
/// Хранилище заданий пакетного импорта архива (ADR-0041): <c>media.import_job</c> и <c>media.import_item</c>.
/// Строки читаются под решёткой допуска смотрящего (<see cref="MediaAccessFilters"/>, ТБ-020); исполнитель задания
/// работает от имени системы с идентификаторами — права и допуск оператора он проверяет сам перед каждым файлом.
/// </summary>
/// <remarks>
/// Счётчики задания меняются в одной транзакции с итогом строки: сводка не пересчитывает строки на каждое обновление
/// экрана и не расходится с ними. Строки нового задания вставляются потоком <c>COPY</c>: опись в сотни тысяч строк
/// через отслеживание сущностей EF вставлялась бы минутами и держала бы их все в памяти.
/// </remarks>
public sealed class MediaImportStore(
    IDbContextFactory<MediaDbContext> contextFactory,
    IAccessPolicy accessPolicy) : IMediaImportStore
{
    private const string CopyItemsCommand =
        "COPY media.import_item (job_id, row_number, relative_path, kind, content_type, byte_size, case_id, case_number, "
        + "classification, division_id, captured_at, captured_date_only, source, place, expected_sha256, status, processing, "
        + "created_at) FROM STDIN (FORMAT BINARY)";

    // Проекция строки для экрана и отчёта — одна на оба чтения.
    private static readonly System.Linq.Expressions.Expression<Func<MediaImportItem, MediaImportItemView>> ToView =
        i => new MediaImportItemView(
            i.Id,
            i.RowNumber,
            i.RelativePath,
            i.CaseId,
            i.CaseNumber,
            i.ByteSize,
            i.Status,
            i.Reason,
            i.AssetId,
            i.ContentSha256,
            i.ExpectedSha256,
            i.Processing,
            i.CapturedAt,
            i.Source,
            i.Place);

    /// <inheritdoc />
    public async Task<int> CreateJobAsync(MediaImportJobDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var job = new MediaImportJob
        {
            FolderPath = draft.FolderPath,
            ManifestFileName = Path.GetFileName(draft.ManifestFileName),
            ManifestSha256 = draft.ManifestSha256.ToUpperInvariant(),
            OperatorUserId = draft.OperatorUserId,
            Status = MediaImportJobStatus.Queued,
            TotalCount = draft.Items.Count,
            BytesTotal = draft.Items.Sum(i => i.ByteSize),
        };
        db.ImportJobs.Add(job);
        await db.SaveChangesAsync(cancellationToken);

        // COPY идёт по тому же соединению и в той же транзакции: задание без строк не появится.
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var now = DateTime.UtcNow;
        await using (var writer = await connection.BeginBinaryImportAsync(CopyItemsCommand, cancellationToken))
        {
            foreach (var item in draft.Items)
            {
                await writer.StartRowAsync(cancellationToken);
                await writer.WriteAsync(job.Id, NpgsqlDbType.Integer, cancellationToken);
                await writer.WriteAsync(item.RowNumber, NpgsqlDbType.Integer, cancellationToken);
                await writer.WriteAsync(item.RelativePath, NpgsqlDbType.Varchar, cancellationToken);
                await writer.WriteAsync((int)item.Kind, NpgsqlDbType.Integer, cancellationToken);
                await writer.WriteAsync(item.ContentType, NpgsqlDbType.Varchar, cancellationToken);
                await writer.WriteAsync(item.ByteSize, NpgsqlDbType.Bigint, cancellationToken);
                await writer.WriteAsync(item.CaseId, NpgsqlDbType.Integer, cancellationToken);
                await writer.WriteAsync(item.CaseNumber, NpgsqlDbType.Varchar, cancellationToken);
                await writer.WriteAsync(item.Classification, NpgsqlDbType.Smallint, cancellationToken);
                await writer.WriteAsync(item.DivisionId, NpgsqlDbType.Integer, cancellationToken);
                await WriteNullableAsync(writer, item.CapturedAt?.ToUniversalTime(), NpgsqlDbType.TimestampTz, cancellationToken);
                await writer.WriteAsync(item.CapturedDateOnly, NpgsqlDbType.Boolean, cancellationToken);
                await WriteNullableAsync(writer, item.Source, NpgsqlDbType.Varchar, cancellationToken);
                await WriteNullableAsync(writer, item.Place, NpgsqlDbType.Varchar, cancellationToken);
                await WriteNullableAsync(writer, item.ExpectedSha256?.ToUpperInvariant(), NpgsqlDbType.Varchar, cancellationToken);
                await writer.WriteAsync((int)MediaImportItemStatus.Pending, NpgsqlDbType.Integer, cancellationToken);
                await writer.WriteAsync((int)MediaImportProcessingState.None, NpgsqlDbType.Integer, cancellationToken);
                await writer.WriteAsync(now, NpgsqlDbType.TimestampTz, cancellationToken);
            }

            await writer.CompleteAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return job.Id;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MediaImportJobSummary>> ListJobsAsync(int take, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var jobs = await db.ImportJobs.AsNoTracking()
            .OrderByDescending(j => j.Id)
            .Take(Math.Clamp(take, 1, 500))
            .ToListAsync(cancellationToken);

        var processing = await CountProcessingAsync(db, jobs.Select(j => j.Id).ToList(), cancellationToken);
        return jobs.Select(j => ToSummary(j, processing)).ToList();
    }

    /// <inheritdoc />
    public async Task<MediaImportJobSummary?> GetJobAsync(int jobId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var job = await db.ImportJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken);
        if (job is null)
        {
            return null;
        }

        var processing = await CountProcessingAsync(db, [jobId], cancellationToken);
        return ToSummary(job, processing);
    }

    /// <inheritdoc />
    public async Task<MediaImportItemPage> ListItemsAsync(
        int jobId, MediaImportItemStatus? status, AccessContext access, int skip, int take,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var all = Filter(db.ImportItems.AsNoTracking(), jobId, status);

        // ТБ-020/021: строки вне допуска смотрящего не выдаются — путь файла и номер дела сами могут быть закрытыми.
        var visible = all.VisibleTo(access, accessPolicy);

        var total = await visible.CountAsync(cancellationToken);
        var hidden = await all.CountAsync(cancellationToken) - total;
        var rows = await visible
            .OrderBy(i => i.RowNumber)
            .Skip(Math.Max(0, skip))
            .Take(Math.Clamp(take, 1, 1000))
            .Select(ToView)
            .ToListAsync(cancellationToken);

        return new MediaImportItemPage(rows, total, hidden);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<MediaImportItemView> StreamItemsAsync(
        int jobId, MediaImportItemStatus? status, AccessContext access,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var rows = Filter(db.ImportItems.AsNoTracking(), jobId, status)
            .VisibleTo(access, accessPolicy)
            .OrderBy(i => i.RowNumber)
            .Select(ToView)
            .AsAsyncEnumerable();

        await foreach (var row in rows.WithCancellation(cancellationToken))
        {
            yield return row;
        }
    }

    /// <inheritdoc />
    public async Task<bool> StopAsync(int jobId, string reason, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        var text = Truncate(reason.Trim(), MediaImportJobConfiguration.StopReasonMaxLength);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var updated = await db.ImportJobs
            .Where(j => j.Id == jobId && (j.Status == MediaImportJobStatus.Queued || j.Status == MediaImportJobStatus.Running))
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, MediaImportJobStatus.Stopped)
                .SetProperty(j => j.StopReason, text)
                .SetProperty(j => j.UpdatedAt, DateTime.UtcNow), cancellationToken);
        return updated > 0;
    }

    /// <inheritdoc />
    public async Task<bool> ResumeAsync(int jobId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var updated = await db.ImportJobs
            .Where(j => j.Id == jobId && j.Status == MediaImportJobStatus.Stopped)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, MediaImportJobStatus.Queued)
                .SetProperty(j => j.StopReason, (string?)null)
                .SetProperty(j => j.UpdatedAt, DateTime.UtcNow), cancellationToken);
        return updated > 0;
    }

    /// <inheritdoc />
    public async Task<int> RecoverInterruptedAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Строка, которую принимал прежний процесс, начинается заново: повтор безопасен — дедупликация по хешу и
        // идемпотентная привязка к делу; свой же носитель распознаётся по ImportItemId.
        var returned = await db.ImportItems
            .Where(i => i.Status == MediaImportItemStatus.InProgress)
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.Status, MediaImportItemStatus.Pending)
                .SetProperty(i => i.UpdatedAt, DateTime.UtcNow), cancellationToken);

        await db.ImportJobs
            .Where(j => j.Status == MediaImportJobStatus.Running)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, MediaImportJobStatus.Queued)
                .SetProperty(j => j.UpdatedAt, DateTime.UtcNow), cancellationToken);

        return returned;
    }

    /// <inheritdoc />
    public async Task<MediaImportJobRun?> StartNextJobAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var job = await db.ImportJobs.AsNoTracking()
            .Where(j => j.Status == MediaImportJobStatus.Running)
            .OrderBy(j => j.Id)
            .Select(j => new MediaImportJobRun(j.Id, j.FolderPath, j.OperatorUserId))
            .FirstOrDefaultAsync(cancellationToken);
        if (job is not null)
        {
            return job;
        }

        // Задания — по одному, в порядке запуска: архив читается с одного диска, параллельное чтение его замедлит.
        var next = await db.ImportJobs.AsNoTracking()
            .Where(j => j.Status == MediaImportJobStatus.Queued)
            .OrderBy(j => j.Id)
            .Select(j => new MediaImportJobRun(j.Id, j.FolderPath, j.OperatorUserId))
            .FirstOrDefaultAsync(cancellationToken);
        if (next is null)
        {
            return null;
        }

        // Условный перевод: остановленное в эту же секунду задание в работу не берётся.
        var started = await db.ImportJobs
            .Where(j => j.Id == next.JobId && j.Status == MediaImportJobStatus.Queued)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, MediaImportJobStatus.Running)
                .SetProperty(j => j.UpdatedAt, DateTime.UtcNow), cancellationToken);
        return started > 0 ? next : null;
    }

    /// <inheritdoc />
    public async Task<MediaImportJobStatus?> GetJobStatusAsync(int jobId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.ImportJobs.AsNoTracking()
            .Where(j => j.Id == jobId)
            .Select(j => (MediaImportJobStatus?)j.Status)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<MediaImportWorkItem?> TakeNextItemAsync(int jobId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var item = await db.ImportItems.AsNoTracking()
            .Where(i => i.JobId == jobId && i.Status == MediaImportItemStatus.Pending)
            .OrderBy(i => i.RowNumber)
            .FirstOrDefaultAsync(cancellationToken);
        if (item is null)
        {
            return null;
        }

        await db.ImportItems
            .Where(i => i.Id == item.Id && i.Status == MediaImportItemStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.Status, MediaImportItemStatus.InProgress)
                .SetProperty(i => i.UpdatedAt, DateTime.UtcNow), cancellationToken);

        return new MediaImportWorkItem(item.Id, item.JobId, new MediaImportItemDraft(
            item.RowNumber,
            item.RelativePath,
            item.Kind,
            item.ContentType,
            item.ByteSize,
            item.CaseId,
            item.CaseNumber,
            item.Classification,
            item.DivisionId,
            item.CapturedAt,
            item.CapturedDateOnly,
            item.Source,
            item.Place,
            item.ExpectedSha256));
    }

    /// <inheritdoc />
    public async Task CompleteItemAsync(int itemId, MediaImportItemOutcome outcome, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        if (outcome.Status is MediaImportItemStatus.Pending or MediaImportItemStatus.InProgress)
        {
            throw new ArgumentException("Итог строки — принят, дубликат или отказ.", nameof(outcome));
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var item = await db.ImportItems.AsNoTracking()
            .Where(i => i.Id == itemId)
            .Select(i => new { i.JobId, i.ByteSize, i.Status })
            .FirstOrDefaultAsync(cancellationToken);

        // Итог пишется один раз: повтор (например, после сбоя записи журнала) счётчики не удваивает.
        if (item is null || item.Status is not (MediaImportItemStatus.Pending or MediaImportItemStatus.InProgress))
        {
            return;
        }

        var now = DateTime.UtcNow;
        var reason = outcome.Reason is null ? null : Truncate(outcome.Reason, MediaImportItemConfiguration.ReasonMaxLength);
        await db.ImportItems
            .Where(i => i.Id == itemId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.Status, outcome.Status)
                .SetProperty(i => i.Reason, reason)
                .SetProperty(i => i.AssetId, outcome.AssetId)
                .SetProperty(i => i.ContentSha256, outcome.ContentSha256)
                .SetProperty(i => i.Processing, outcome.Processing)
                .SetProperty(i => i.CompletedAt, now)
                .SetProperty(i => i.UpdatedAt, now), cancellationToken);

        var accepted = outcome.Status == MediaImportItemStatus.Accepted ? 1 : 0;
        var duplicate = outcome.Status == MediaImportItemStatus.Duplicate ? 1 : 0;
        var rejected = outcome.Status == MediaImportItemStatus.Rejected ? 1 : 0;
        var bytes = item.ByteSize;
        await db.ImportJobs
            .Where(j => j.Id == item.JobId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.AcceptedCount, j => j.AcceptedCount + accepted)
                .SetProperty(j => j.DuplicateCount, j => j.DuplicateCount + duplicate)
                .SetProperty(j => j.RejectedCount, j => j.RejectedCount + rejected)
                .SetProperty(j => j.BytesDone, j => j.BytesDone + bytes)
                .SetProperty(j => j.UpdatedAt, now), cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task ReturnItemAsync(int itemId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await db.ImportItems
            .Where(i => i.Id == itemId && i.Status == MediaImportItemStatus.InProgress)
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.Status, MediaImportItemStatus.Pending)
                .SetProperty(i => i.UpdatedAt, DateTime.UtcNow), cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> TryCompleteJobAsync(int jobId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var now = DateTime.UtcNow;
        var completed = await db.ImportJobs
            .Where(j => j.Id == jobId
                && j.Status == MediaImportJobStatus.Running
                && !db.ImportItems.Any(i => i.JobId == jobId
                    && (i.Status == MediaImportItemStatus.Pending || i.Status == MediaImportItemStatus.InProgress)))
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, MediaImportJobStatus.Completed)
                .SetProperty(j => j.FinishedAt, now)
                .SetProperty(j => j.UpdatedAt, now), cancellationToken);
        return completed > 0;
    }

    /// <inheritdoc />
    public async Task<bool> IsAssetFromItemAsync(int assetId, int itemId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Assets.AsNoTracking().AnyAsync(a => a.Id == assetId && a.ImportItemId == itemId, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int> RecoverProcessingAsync(Func<Guid, Task<bool>> isCompleted, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(isCompleted);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var now = DateTime.UtcNow;

        // Носитель уничтожен (ТБ-075) до обработки — подавать нечего.
        await db.ImportItems
            .Where(i => (i.Processing == MediaImportProcessingState.Waiting || i.Processing == MediaImportProcessingState.Queued)
                && i.AssetId == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.Processing, MediaImportProcessingState.Done)
                .SetProperty(i => i.UpdatedAt, now), cancellationToken);

        var queued = await db.ImportItems.AsNoTracking()
            .Where(i => i.Processing == MediaImportProcessingState.Queued)
            .Select(i => new { i.Id, i.IndexTaskId, i.TranscriptTaskId })
            .ToListAsync(cancellationToken);

        var returned = 0;
        foreach (var item in queued)
        {
            var done = (item.IndexTaskId is not { } index || await isCompleted(index))
                && (item.TranscriptTaskId is not { } transcript || await isCompleted(transcript));
            await db.ImportItems
                .Where(i => i.Id == item.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(i => i.Processing, done ? MediaImportProcessingState.Done : MediaImportProcessingState.Waiting)
                    .SetProperty(i => i.IndexTaskId, done ? item.IndexTaskId : null)
                    .SetProperty(i => i.TranscriptTaskId, done ? item.TranscriptTaskId : null)
                    .SetProperty(i => i.UpdatedAt, now), cancellationToken);
            returned += done ? 0 : 1;
        }

        return returned;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MediaImportFeedItem>> ListQueuedProcessingAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await FeedQuery(db, MediaImportProcessingState.Queued)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MediaImportFeedItem>> ListWaitingProcessingAsync(int take, CancellationToken cancellationToken = default)
    {
        if (take <= 0)
        {
            return [];
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await FeedQuery(db, MediaImportProcessingState.Waiting)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task MarkProcessingQueuedAsync(
        int itemId, Guid? indexTaskId, Guid? transcriptTaskId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await db.ImportItems
            .Where(i => i.Id == itemId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.Processing, MediaImportProcessingState.Queued)
                .SetProperty(i => i.IndexTaskId, indexTaskId)
                .SetProperty(i => i.TranscriptTaskId, transcriptTaskId)
                .SetProperty(i => i.UpdatedAt, DateTime.UtcNow), cancellationToken);
    }

    /// <inheritdoc />
    public async Task MarkProcessingDoneAsync(int itemId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await db.ImportItems
            .Where(i => i.Id == itemId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(i => i.Processing, MediaImportProcessingState.Done)
                .SetProperty(i => i.UpdatedAt, DateTime.UtcNow), cancellationToken);
    }

    // Носитель берётся из хранилища, а не из строки: вид мог измениться после приёма (видео без видеопотока
    // индексатор переводит в аудио, ADR-0026) — тогда нужна только расшифровка.
    private static IQueryable<MediaImportFeedItem> FeedQuery(MediaDbContext db, MediaImportProcessingState state) =>
        db.ImportItems.AsNoTracking()
            .Where(i => i.Processing == state && i.AssetId != null)
            .OrderBy(i => i.JobId)
            .ThenBy(i => i.RowNumber)
            .Select(i => new MediaImportFeedItem(i.Id, i.AssetId!.Value, i.Asset!.Kind, i.IndexTaskId, i.TranscriptTaskId));

    private static IQueryable<MediaImportItem> Filter(IQueryable<MediaImportItem> items, int jobId, MediaImportItemStatus? status)
    {
        var query = items.Where(i => i.JobId == jobId);
        return status is { } wanted ? query.Where(i => i.Status == wanted) : query;
    }

    private static async Task<Dictionary<(int JobId, MediaImportProcessingState State), int>> CountProcessingAsync(
        MediaDbContext db, List<int> jobIds, CancellationToken cancellationToken)
    {
        if (jobIds.Count == 0)
        {
            return [];
        }

        var counts = await db.ImportItems.AsNoTracking()
            .Where(i => jobIds.Contains(i.JobId) && i.Processing != MediaImportProcessingState.None)
            .GroupBy(i => new { i.JobId, i.Processing })
            .Select(g => new { g.Key.JobId, g.Key.Processing, Count = g.Count() })
            .ToListAsync(cancellationToken);
        return counts.ToDictionary(c => (c.JobId, c.Processing), c => c.Count);
    }

    private static MediaImportJobSummary ToSummary(
        MediaImportJob job, Dictionary<(int JobId, MediaImportProcessingState State), int> processing) =>
        new(
            job.Id,
            job.CreatedAt,
            job.OperatorUserId,
            job.FolderPath,
            job.ManifestFileName,
            job.ManifestSha256,
            job.Status,
            job.StopReason,
            job.FinishedAt,
            job.TotalCount,
            job.AcceptedCount,
            job.DuplicateCount,
            job.RejectedCount,
            job.BytesTotal,
            job.BytesDone,
            processing.GetValueOrDefault((job.Id, MediaImportProcessingState.Waiting)),
            processing.GetValueOrDefault((job.Id, MediaImportProcessingState.Queued)),
            processing.GetValueOrDefault((job.Id, MediaImportProcessingState.Done)));

    private static Task WriteNullableAsync<T>(
        NpgsqlBinaryImporter writer, T? value, NpgsqlDbType type, CancellationToken cancellationToken)
        where T : struct =>
        value is { } present ? writer.WriteAsync(present, type, cancellationToken) : writer.WriteNullAsync(cancellationToken);

    private static Task WriteNullableAsync(
        NpgsqlBinaryImporter writer, string? value, NpgsqlDbType type, CancellationToken cancellationToken) =>
        value is null ? writer.WriteNullAsync(cancellationToken) : writer.WriteAsync(value, type, cancellationToken);

    private static string Truncate(string value, int maxLength) => value.Length > maxLength ? value[..maxLength] : value;
}
