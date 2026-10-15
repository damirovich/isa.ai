using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Data.Entities;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Modules.Media.Data;

/// <summary>
/// Слой пометок фотографий поверх <c>media.annotation</c> (ТФ-МЕД-13, ADR-0050).
/// </summary>
/// <remarks>
/// ИНВАРИАНТ (ТБ-020/021): каждое чтение и изменение — под решёткой пакета (<see cref="MediaAccessFilters.VisibleTo{T}"/>)
/// по строке пометки и по строке носителя; пометка ставится только на видимое субъекту ФОТО, с его грифом и
/// подразделением (ТБ-070). Сужение по делам субъекта (ТБ-071) проверяет обработчик до вызова хранилища.
/// </remarks>
public sealed class MediaAnnotationStore(IDbContextFactory<MediaDbContext> contextFactory, IAccessPolicy accessPolicy) : IMediaAnnotationStore
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<AnnotationRow>> ListAsync(int assetId, AccessContext access, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.Annotations.AsNoTracking().VisibleTo(access, accessPolicy)
            .Where(a => a.AssetId == assetId)
            .OrderBy(a => a.Id)
            .Select(a => new AnnotationRow(a.Id, a.AssetId, a.Kind, a.X1, a.Y1, a.X2, a.Y2, a.Text, a.Color, a.AuthorUserId, a.CreatedAt))
            .ToListAsync(cancellationToken);
        return rows.Select(r => r with { CreatedAt = DateTime.SpecifyKind(r.CreatedAt, DateTimeKind.Utc) }).ToList();
    }

    /// <inheritdoc />
    public async Task<int?> AddAsync(AnnotationDraft draft, int? authorUserId, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Режим пометки — носителя (ТБ-070); носитель вне решётки или не фото — неотличимо от отсутствующего.
        var asset = await db.Assets.AsNoTracking().VisibleTo(access, accessPolicy)
            .Where(a => a.Id == draft.AssetId && a.Kind == MediaKind.Image)
            .Select(a => new { a.Classification, a.DivisionId })
            .FirstOrDefaultAsync(cancellationToken);
        if (asset is null)
        {
            return null;
        }

        var entity = new MediaAnnotation
        {
            AssetId = draft.AssetId,
            Kind = draft.Kind,
            X1 = draft.X1,
            Y1 = draft.Y1,
            X2 = draft.X2,
            Y2 = draft.Y2,
            Text = string.IsNullOrWhiteSpace(draft.Text) ? null : draft.Text.Trim(),
            Color = draft.Color.ToUpperInvariant(),
            AuthorUserId = authorUserId,
            Classification = asset.Classification,
            DivisionId = asset.DivisionId,
        };
        db.Annotations.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        return entity.Id;
    }

    /// <inheritdoc />
    public async Task<int?> GetAssetIdAsync(int annotationId, AccessContext access, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Annotations.AsNoTracking().VisibleTo(access, accessPolicy)
            .Where(a => a.Id == annotationId)
            .Select(a => (int?)a.AssetId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(int annotationId, AccessContext access, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var deleted = await db.Annotations.VisibleTo(access, accessPolicy)
            .Where(a => a.Id == annotationId)
            .ExecuteDeleteAsync(cancellationToken);
        return deleted > 0;
    }
}
