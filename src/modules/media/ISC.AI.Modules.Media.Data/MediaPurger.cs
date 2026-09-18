using System.Globalization;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Enums;
using ISC.AI.Abstractions.Storage;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Modules.Media.Data;

/// <summary>
/// Гарантированное удаление носителя и всех производных из схемы <c>media</c> и файлового хранилища
/// (ТБ-064/075, GATE-6). Физическое удаление (не мягкое): носитель/кадр/лицо/шаблон намеренно
/// НЕ <c>ISoftDeletable</c>. Строки снимаются каскадом БД (<c>ON DELETE CASCADE</c> внутри схемы).
/// </summary>
/// <remarks>
/// Порядок FAIL-CLOSED (как у <c>DocumentPurger</c> ядра): запись в неизменяемый аудит идёт ПЕРВОЙ —
/// недоступен журнал — исключение, удаление не выполняется (нет уничтожения биометрии без записи).
/// Затем одна транзакция БД (частичного результата нет), и только ПОСЛЕ её фиксации — файлы:
/// осиротевший файл без строки безвреден (недостижим через API, подбирается уборкой), тогда как
/// строка без файла или файл при откате транзакции — дефект.
/// </remarks>
public sealed class MediaPurger(
    IDbContextFactory<MediaDbContext> contextFactory,
    IAuditWriter auditWriter,
    IFileStorage fileStorage) : IMediaPurger
{
    /// <inheritdoc />
    public async Task<MediaPurgeResult> PurgeAsync(
        int assetId, int? subjectId = null, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var asset = await db.Assets
            .Where(a => a.Id == assetId)
            .Select(a => new { a.Classification, a.DivisionId, a.StoredFileName })
            .FirstOrDefaultAsync(cancellationToken);

        if (asset is null)
        {
            return MediaPurgeResult.NotFound; // идемпотентно: удалять нечего, аудит не пишем
        }

        // Имена вырезок собираем ДО удаления строк — после каскада их уже негде взять.
        var crops = await db.Faces
            .Where(f => f.AssetId == assetId && f.CropStoredFileName != null)
            .Select(f => f.CropStoredFileName!)
            .ToListAsync(cancellationToken);
        var faceCount = await db.Faces.CountAsync(f => f.AssetId == assetId, cancellationToken);

        // FAIL-CLOSED (ТБ-064): аудит ДО уничтожения данных. Недоступен журнал — WriteAsync бросит и
        // удаление НЕ произойдёт.
        await auditWriter.WriteAsync(
            new AuditEntry(
                AuditAction.Purge,
                asset.Classification,
                SubjectId: subjectId,
                ObjectRef: "media:asset:" + assetId.ToString(CultureInfo.InvariantCulture),
                DivisionId: asset.DivisionId,
                PayloadSensitive:
                    $"Гарантированное удаление носителя и биометрических производных: лиц {faceCount}, вырезок {crops.Count} (ТБ-064/075)."),
            cancellationToken);

        // Атомарно: носитель → кадры/лица/шаблоны каскадом БД (см. конфигурации).
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Assets.Where(a => a.Id == assetId).ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        // Файлы — после фиксации. DeleteAsync идемпотентен (ADR-0018): отсутствующий файл — не ошибка.
        var subPath = assetId.ToString(CultureInfo.InvariantCulture);
        var filesRemoved = 0;
        await fileStorage.DeleteAsync(asset.StoredFileName, MediaFileCategories.Originals, subPath, cancellationToken);
        filesRemoved++;
        foreach (var crop in crops)
        {
            await fileStorage.DeleteAsync(crop, MediaFileCategories.FaceCrops, subPath, cancellationToken);
            filesRemoved++;
        }

        return new MediaPurgeResult(Found: true, FacesRemoved: faceCount, FilesRemoved: filesRemoved);
    }
}
