using System.Globalization;
using ISC.AI.Abstractions.Corpus;
using ISC.AI.Abstractions.Ingestion;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Data.Entities;
using ISC.AI.Modules.Media.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Modules.Media.Data;

/// <summary>Индексация расшифровок в корпус ядра (ADR-0051): включена ли.</summary>
/// <param name="Enabled">Индексировать ли готовые расшифровки (по умолчанию да).</param>
public sealed record TranscriptCorpusOptions(bool Enabled = true)
{
    /// <summary>Ключ конфигурации: <c>false</c> выключает индексацию (чат и поиск по смыслу расшифровок не видят).</summary>
    public const string EnabledKey = "Media:Transcripts:CorpusIndexing";
}

/// <summary>
/// Расшифровки в корпусе ядра поверх <c>media.transcript_index_link</c> (ADR-0051).
/// </summary>
/// <remarks>
/// ИНВАРИАНТЫ (ТБ-020/064/070). Документ корпуса получает гриф и подразделение НОСИТЕЛЯ — их же получают его
/// фрагменты и векторы, и floor допуска ядра работает без знания о носителе. Выдача области — под решёткой пакета.
/// Переиндексация (новая расшифровка) сначала кладёт новую версию, затем УНИЧТОЖАЕТ прежнюю (<see cref="IDocumentPurger"/>,
/// запись акта в журнале), а не оставляет её «неактуальной»: устаревший текст расшифровки — тоже материал, и при
/// уничтожении носителя его было бы не найти. Носитель уничтожен во время индексации — новый документ корпуса
/// уничтожается сразу, «сиротой» не остаётся.
/// </remarks>
public sealed class TranscriptCorpus(
    IDbContextFactory<MediaDbContext> contextFactory,
    IAccessPolicy accessPolicy,
    IIngestionPort ingestion,
    IDocumentPurger purger,
    TranscriptCorpusOptions options) : ITranscriptCorpus
{
    /// <inheritdoc />
    public async Task<TranscriptCorpusScope> ResolveAsync(
        IReadOnlyCollection<int> assetIds, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assetIds);
        ArgumentNullException.ThrowIfNull(access);

        var ids = assetIds.Distinct().ToArray();
        if (ids.Length == 0)
        {
            return new TranscriptCorpusScope([], 0);
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var visible = db.Assets.AsNoTracking().VisibleTo(access, accessPolicy).Where(a => ids.Contains(a.Id));
        var transcribed = await visible.CountAsync(a => db.TranscriptSegments.Any(s => s.AssetId == a.Id), cancellationToken);
        var indexed = await visible
            .Join(db.TranscriptIndexLinks.AsNoTracking(), a => a.Id, l => l.AssetId,
                (a, l) => new TranscriptCorpusRef(a.Id, l.CoreDocumentId, a.OriginalFileName, a.Kind, a.Classification))
            .ToListAsync(cancellationToken);
        return new TranscriptCorpusScope(indexed, transcribed);
    }

    /// <inheritdoc />
    public async Task<TranscriptIndexResult> IndexAsync(int assetId, CancellationToken cancellationToken = default)
    {
        if (!options.Enabled)
        {
            return new TranscriptIndexResult(TranscriptIndexStatus.Disabled);
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var asset = await db.Assets.AsNoTracking()
            .Where(a => a.Id == assetId)
            .Select(a => new { a.Id, a.OriginalFileName, a.CapturedAt, a.Classification, a.DivisionId })
            .FirstOrDefaultAsync(cancellationToken);
        if (asset is null)
        {
            return new TranscriptIndexResult(TranscriptIndexStatus.NotFound);
        }

        var segments = await db.TranscriptSegments.AsNoTracking()
            .Where(s => s.AssetId == assetId)
            .OrderBy(s => s.Index)
            .Select(s => new { s.StartMs, s.Text })
            .ToListAsync(cancellationToken);
        var link = await db.TranscriptIndexLinks.FirstOrDefaultAsync(l => l.AssetId == assetId, cancellationToken);

        // Расшифровки нет (или речь не обнаружена): в корпусе её быть не должно.
        if (segments.Count == 0)
        {
            if (link is not null)
            {
                await purger.PurgeAsync(link.CoreDocumentId, subjectId: null, cancellationToken);
                db.TranscriptIndexLinks.Remove(link);
                await db.SaveChangesAsync(cancellationToken);
            }

            return new TranscriptIndexResult(TranscriptIndexStatus.Empty);
        }

        var number = asset.Id.ToString(CultureInfo.InvariantCulture);
        var text = TranscriptCorpusText.Build(
            asset.Id, asset.OriginalFileName, asset.CapturedAt?.UtcDateTime, segments.Select(s => (s.StartMs, s.Text)));
        var result = await ingestion.IngestAsync(
            new IngestionRequest(
                TranscriptCorpusText.DocType,
                $"Расшифровка «{asset.OriginalFileName}» · носитель № {number}",
                text,
                asset.Classification,
                asset.DivisionId,
                Source: TranscriptCorpusText.Source,
                DocDate: asset.CapturedAt is { } captured ? DateOnly.FromDateTime(captured.UtcDateTime) : null,
                Metadata: new Dictionary<string, string> { [TranscriptCorpusText.AssetIdKey] = number }),
            cancellationToken);
        if (!result.Accepted || result.DocumentId is not { } coreDocumentId)
        {
            return new TranscriptIndexResult(TranscriptIndexStatus.Rejected, Reason: result.RejectionReason);
        }

        var now = DateTime.UtcNow;
        int? replaced = null;
        if (link is null)
        {
            db.TranscriptIndexLinks.Add(new TranscriptIndexLink { AssetId = assetId, CoreDocumentId = coreDocumentId, IndexedAt = now });
        }
        else
        {
            replaced = link.CoreDocumentId == coreDocumentId ? null : link.CoreDocumentId;
            link.CoreDocumentId = coreDocumentId;
            link.IndexedAt = now;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Носитель уничтожен, пока строился текст: новый документ корпуса — сирота, уничтожаем сразу (ТБ-064).
            await purger.PurgeAsync(coreDocumentId, subjectId: null, cancellationToken);
            return new TranscriptIndexResult(TranscriptIndexStatus.NotFound);
        }

        // Прежняя версия — после записи новой: поиск не остаётся без расшифровки ни на миг.
        if (replaced is { } old)
        {
            await purger.PurgeAsync(old, subjectId: null, cancellationToken);
        }

        return new TranscriptIndexResult(
            result.ChunkCount == 0 && replaced is null && link is not null ? TranscriptIndexStatus.Unchanged : TranscriptIndexStatus.Indexed,
            coreDocumentId);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<int>> ListPendingAsync(int limit, CancellationToken cancellationToken = default)
    {
        if (!options.Enabled || limit <= 0)
        {
            return [];
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Assets.AsNoTracking()
            .Where(a => a.TranscribedAt != null
                && db.TranscriptSegments.Any(s => s.AssetId == a.Id)
                && !db.TranscriptIndexLinks.Any(l => l.AssetId == a.Id && l.IndexedAt >= a.TranscribedAt))
            .OrderBy(a => a.Id)
            .Select(a => a.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }
}
