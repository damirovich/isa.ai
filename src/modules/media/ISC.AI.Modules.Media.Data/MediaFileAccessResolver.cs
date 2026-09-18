using System.Globalization;
using ISC.AI.Modules.Media.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Modules.Media.Data;

/// <summary>
/// Разрешение имени файла носителя/вырезки/вырезки пробы в описание с режимными полями (ТБ-073) для
/// эндпоинта раздачи. Для категории <see cref="MediaFileCategories.Probes"/> параметр «носитель» маршрута —
/// идентификатор ПОИСКОВОЙ СЕССИИ, режимные поля — сессии (гриф дела, ТБ-070), а <see cref="MediaFileDescriptor.CaseRef"/> —
/// дело сессии (для проверки области дел субъекта в эндпоинте, ТБ-071). Файл, принадлежащий ДРУГОМУ носителю, чем указан в маршруте, не разрешается — наружу
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

            case MediaFileCategories.Probes:
            {
                // Вырезка пробы принадлежит СЕССИИ: параметр маршрута — идентификатор сессии, режим — сессии
                // (гриф дела, ТБ-070). Файл лежит в подкаталоге ДЕЛА (идентификатор дела известен ДО создания
                // сессии, идентификатор сессии — нет), поэтому SubPath = CaseId. Вектор пробы в базе нет (ТБ-074).
                var session = await db.SearchSessions
                    .Where(s => s.Id == assetId && s.ProbeCropStoredFileName == storedFileName)
                    .Select(s => new { s.CaseId, s.Classification, s.DivisionId })
                    .FirstOrDefaultAsync(cancellationToken);
                // CaseRef — дело сессии: эндпоинт проверит по нему область дел субъекта (ТБ-071) — у вырезки
                // пробы нет носителя, через который это можно было бы сделать.
                return session is null
                    ? null
                    : new MediaFileDescriptor(
                        storedFileName, category, ProbeSubPath(session.CaseId), "image/jpeg",
                        session.Classification, session.DivisionId, CaseRef: session.CaseId);
            }

            default:
                return null;
        }
    }

    /// <summary>
    /// Подкаталог хранилища для вырезок проб (категория <see cref="MediaFileCategories.Probes"/>): идентификатор
    /// ДЕЛА. Сценарий поиска обязан сохранять вырезку пробы под этим же подкаталогом до создания сессии.
    /// </summary>
    public static string ProbeSubPath(int caseId) => caseId.ToString(CultureInfo.InvariantCulture);
}
