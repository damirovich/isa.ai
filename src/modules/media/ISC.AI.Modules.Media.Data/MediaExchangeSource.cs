using System.Globalization;
using ISC.AI.Abstractions.Storage;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Modules.Media.Data;

/// <summary>
/// Носители для пакета обмена между офисами (ADR-0068): реквизиты, лица и решения верификации — без шаблонов и векторов
/// (ТБ-076); файл оригинала — из файлового хранилища.
/// </summary>
/// <remarks>
/// ИНВАРИАНТ (ТБ-079, ТБ-083): решётка допуска не применяется — см. <see cref="IMediaExchangeSource"/>. Таблицы шаблонов
/// (<c>media.face_template</c>) и частей индексации (<c>media.index_checkpoint</c>, в них шаблоны) здесь не читаются вовсе.
/// </remarks>
public sealed class MediaExchangeSource(IDbContextFactory<MediaDbContext> contextFactory, IFileStorage storage) : IMediaExchangeSource
{
    // Списки номеров — порциями: IN с десятками тысяч параметров упирается в предел протокола PostgreSQL.
    private const int Batch = 2_000;

    /// <inheritdoc />
    public async Task<IReadOnlyList<MediaExchangeAsset>> GetAssetsAsync(
        IReadOnlyCollection<int> assetIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assetIds);
        var result = new List<MediaExchangeAsset>(assetIds.Count);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        foreach (var chunk in assetIds.Distinct().Chunk(Batch))
        {
            result.AddRange(await db.Assets.AsNoTracking()
                .Where(a => chunk.Contains(a.Id))
                .Select(a => new MediaExchangeAsset(
                    a.Id, a.Kind, a.OriginalFileName, a.ContentType, a.ContentHash, a.ByteSize, a.DurationMs, a.Source,
                    a.CapturedAt, a.RecordedAt, a.FrameRate, a.FrameWidth, a.FrameHeight, a.UploadedByUserId,
                    a.SourceAssetId, a.SourceTimestampMs, a.EditOperations, a.Classification, a.DivisionId, a.CreatedAt))
                .ToListAsync(cancellationToken));
        }

        return result.OrderBy(a => a.AssetId).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MediaExchangeFace>> GetFacesAsync(
        IReadOnlyCollection<int> faceIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(faceIds);
        var result = new List<MediaExchangeFace>(faceIds.Count);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        foreach (var chunk in faceIds.Distinct().Chunk(Batch))
        {
            // Фото кадров не имеет — номер и таймкод 0.
            result.AddRange(await db.Faces.AsNoTracking()
                .Where(f => chunk.Contains(f.Id))
                .Select(f => new MediaExchangeFace(
                    f.Id, f.AssetId, f.Frame == null ? 0 : f.Frame.Index, f.Frame == null ? 0 : f.Frame.TimestampMs,
                    f.BoxX, f.BoxY, f.BoxWidth, f.BoxHeight))
                .ToListAsync(cancellationToken));
        }

        return result.OrderBy(f => f.FaceId).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MediaExchangeDecision>> GetDecisionsAsync(
        IReadOnlyCollection<int> candidateIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidateIds);
        var result = new List<MediaExchangeDecision>();
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        foreach (var chunk in candidateIds.Distinct().Chunk(Batch))
        {
            result.AddRange(await db.VerificationDecisions.AsNoTracking()
                .Where(d => chunk.Contains(d.CandidateId))
                .Select(d => new MediaExchangeDecision(
                    d.CandidateId, d.Stage, d.Verdict, d.Rationale, d.DecidedAtUtc, d.SubjectId, d.ConfirmationMode))
                .ToListAsync(cancellationToken));
        }

        return result.OrderBy(d => d.CandidateId).ThenBy(d => d.DecidedAtUtc).ToList();
    }

    /// <inheritdoc />
    public async Task<Stream> OpenOriginalAsync(int assetId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var storedFileName = await db.Assets.AsNoTracking()
            .Where(a => a.Id == assetId)
            .Select(a => a.StoredFileName)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new FileNotFoundException($"Носителя № {assetId.ToString(CultureInfo.InvariantCulture)} нет.");

        return await storage.OpenReadAsync(
            storedFileName, MediaFileCategories.Originals, assetId.ToString(CultureInfo.InvariantCulture), cancellationToken);
    }
}
