using System.Globalization;
using ISC.AI.Modules.Media.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Modules.Media.Data;

/// <summary>
/// Разрешение имени файла носителя/вырезки в описание с режимными полями (ТБ-073) для эндпоинта
/// раздачи. Файл, принадлежащий ДРУГОМУ носителю, чем указан в маршруте, не разрешается — наружу
/// единый «не найден» (как у <c>DocumentFileAccessResolver</c> документооборота).
/// </summary>
public sealed class MediaFileAccessResolver(IDbContextFactory<MediaDbContext> contextFactory) : IMediaFileAccess
{
    /// <inheritdoc />
    public async Task<MediaFileDescriptor?> ResolveAsync(
        string category, int assetId, string storedFileName, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var subPath = assetId.ToString(CultureInfo.InvariantCulture);

        switch (category)
        {
            case MediaFileCategories.Originals:
            {
                var asset = await db.Assets
                    .Where(a => a.Id == assetId && a.StoredFileName == storedFileName)
                    .Select(a => new { a.ContentType, a.Classification, a.DivisionId })
                    .FirstOrDefaultAsync(cancellationToken);
                return asset is null
                    ? null
                    : new MediaFileDescriptor(
                        storedFileName, category, subPath, asset.ContentType, asset.Classification, asset.DivisionId);
            }

            case MediaFileCategories.FaceCrops:
            {
                var face = await db.Faces
                    .Where(f => f.AssetId == assetId && f.CropStoredFileName == storedFileName)
                    .Select(f => new { f.Classification, f.DivisionId })
                    .FirstOrDefaultAsync(cancellationToken);
                return face is null
                    ? null
                    : new MediaFileDescriptor(
                        storedFileName, category, subPath, "image/jpeg", face.Classification, face.DivisionId);
            }

            default:
                return null;
        }
    }
}
