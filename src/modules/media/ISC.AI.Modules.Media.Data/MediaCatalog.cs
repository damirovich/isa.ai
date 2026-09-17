using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Data.Entities;
using ISC.AI.Modules.Media.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Modules.Media.Data;

/// <summary>
/// Чтение носителей, лиц и шаблонов (ТФ-МЕД-03, ТФ-ПЛ-03) под решёткой доступа НА СТОРОНЕ БД
/// (ТБ-020/070): каждый запрос проходит через <see cref="MediaAccessFilters.VisibleTo{T}"/> — floor ядра
/// плюс политика профиля. Недоступная строка неотличима от несуществующей (<see langword="null"/>/пусто).
/// </summary>
/// <param name="contextFactory">Фабрика контекста (на операцию, ТС-008).</param>
/// <param name="accessPolicy">Сужающая политика профиля (ADR-0014).</param>
public sealed class MediaCatalog(
    IDbContextFactory<MediaDbContext> contextFactory,
    IAccessPolicy accessPolicy) : IMediaCatalog
{
    /// <inheritdoc />
    public async Task<MediaAssetRow?> GetAsync(int assetId, AccessContext access, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await ProjectAssets(db, db.Assets.AsNoTracking().VisibleTo(access, accessPolicy).Where(a => a.Id == assetId))
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MediaAssetRow>> ListAsync(
        IReadOnlyCollection<int> assetIds, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assetIds);
        if (assetIds.Count == 0)
        {
            return [];
        }

        var ids = assetIds as int[] ?? assetIds.ToArray();
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await ProjectAssets(
                db,
                db.Assets.AsNoTracking().VisibleTo(access, accessPolicy).Where(a => ids.Contains(a.Id))
                    .OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.Id))
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FaceRow>> ListFacesAsync(int assetId, AccessContext access, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Гриф/подразделение лица денормализованы с носителя (ТБ-020): решётка по строке лица эквивалентна
        // решётке по носителю, джойн для проверки доступа не нужен.
        // Сортировка ДО проекции (после неё EF порядок не транслирует): кадр (изображение — первым), позиция.
        return await ProjectFaces(
                db.Faces.AsNoTracking().VisibleTo(access, accessPolicy).Where(f => f.AssetId == assetId)
                    .OrderBy(f => f.Frame != null ? f.Frame.Index : -1).ThenBy(f => f.BoxX).ThenBy(f => f.Id))
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<FaceRow?> GetFaceAsync(int faceId, AccessContext access, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await ProjectFaces(db.Faces.AsNoTracking().VisibleTo(access, accessPolicy).Where(f => f.Id == faceId))
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Вектор отдаётся ТОЛЬКО для внутреннего сценария «этот человек в других материалах» (ТФ-ПЛ-03) и
    /// только если шаблон в допуске субъекта (ТБ-020/070). Экспорт векторов наружу запрещён (ТБ-076):
    /// вызывающий сценарий использует его как пробу и не выдаёт в UI/файлы.
    /// </remarks>
    public async Task<float[]?> GetTemplateAsync(int faceId, AccessContext access, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var embedding = await db.Templates.AsNoTracking()
            .VisibleTo(access, accessPolicy)
            .Where(t => t.FaceId == faceId)
            .Select(t => t.Embedding)
            .FirstOrDefaultAsync(cancellationToken);
        return embedding?.ToArray();
    }

    // Проекции — единственное место, где строка БД превращается в строку чтения; FaceCount — подзапрос.
    private static IQueryable<MediaAssetRow> ProjectAssets(MediaDbContext db, IQueryable<MediaAsset> assets) =>
        assets.Select(a => new MediaAssetRow(
            a.Id,
            a.Kind,
            a.OriginalFileName,
            a.StoredFileName,
            a.ContentType,
            a.ByteSize,
            a.DurationMs,
            a.Source,
            a.CapturedAt,
            a.Classification,
            a.DivisionId,
            a.UploadedByUserId,
            a.IndexStatus,
            a.IndexError,
            a.DetectorVersion,
            a.EmbedderVersion,
            a.IndexedAt,
            a.CreatedAt,
            db.Faces.Count(f => f.AssetId == a.Id)));

    private static IQueryable<FaceRow> ProjectFaces(IQueryable<Face> faces) =>
        faces.Select(f => new FaceRow(
            f.Id,
            f.AssetId,
            f.Frame != null ? f.Frame.Index : (int?)null,
            f.Frame != null ? f.Frame.TimestampMs : (long?)null,
            f.BoxX,
            f.BoxY,
            f.BoxWidth,
            f.BoxHeight,
            f.DetectionScore,
            f.QualityScore,
            f.QualityAcceptable,
            f.QualityReason,
            f.CropStoredFileName,
            f.TrackId,
            f.Classification,
            f.DivisionId));
}
