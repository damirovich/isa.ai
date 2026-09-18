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

    /// <inheritdoc />
    public async Task<TemplatePurgeResult> PurgeTemplatesAsync(
        IReadOnlyCollection<int> assetIds,
        string reason,
        int? subjectId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assetIds);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        if (assetIds.Count == 0)
        {
            return TemplatePurgeResult.Empty;
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Что именно снимаем — считаем ДО удаления: после каскада ни чисел, ни имён файлов не останется,
        // а они идут в акт (ТБ-074) и в журнал.
        var targets = await db.Assets
            .Where(a => assetIds.Contains(a.Id))
            .Select(a => new
            {
                a.Id,
                a.Classification,
                a.DivisionId,
                Templates = db.Templates.Count(t => t.AssetId == a.Id),
                Crops = db.Faces
                    .Where(f => f.AssetId == a.Id && f.CropStoredFileName != null)
                    .Select(f => f.CropStoredFileName!)
                    .ToList(),
            })
            .Where(a => a.Templates > 0 || a.Crops.Count > 0)
            .ToListAsync(cancellationToken);

        if (targets.Count == 0)
        {
            return TemplatePurgeResult.Empty; // идемпотентно: биометрии уже нет — ни удаления, ни записи
        }

        // FAIL-CLOSED (ТБ-064/074): аудит ДО уничтожения, по каждому носителю отдельной записью — с его
        // собственным грифом и подразделением. Общая запись «по делу» усреднила бы гриф, а журнал грифов
        // не усредняет. Недоступен журнал — WriteAsync бросит, и ни один шаблон не будет удалён.
        foreach (var target in targets)
        {
            await auditWriter.WriteAsync(
                new AuditEntry(
                    AuditAction.Purge,
                    target.Classification,
                    SubjectId: subjectId,
                    ObjectRef: "media:asset:" + target.Id.ToString(CultureInfo.InvariantCulture),
                    DivisionId: target.DivisionId,
                    PayloadSensitive:
                        $"Удаление биометрических шаблонов носителя ({reason}): шаблонов {target.Templates}, "
                        + $"вырезок {target.Crops.Count}. Носитель, кадры и лица сохранены (ТБ-074, ТФ-ДЕЛ-04)."),
                cancellationToken);
        }

        var ids = targets.Select(t => t.Id).ToList();

        // Атомарно: шаблоны снимаются целиком (вектор уходит из индекса вместе со строкой), у лиц
        // обнуляется ссылка на вырезку — сама строка лица остаётся: это «появление» фигуранта,
        // результат работы по делу (ТФ-ПЕР-02), а не биометрический материал поиска.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var templatesRemoved = await db.Templates
            .Where(t => ids.Contains(t.AssetId))
            .ExecuteDeleteAsync(cancellationToken);
        await db.Faces
            .Where(f => ids.Contains(f.AssetId) && f.CropStoredFileName != null)
            .ExecuteUpdateAsync(set => set.SetProperty(f => f.CropStoredFileName, (string?)null), cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        // Файлы — после фиксации: осиротевший файл без строки безвреден, строка без файла — дефект.
        var cropsRemoved = 0;
        foreach (var target in targets)
        {
            var subPath = target.Id.ToString(CultureInfo.InvariantCulture);
            foreach (var crop in target.Crops)
            {
                await fileStorage.DeleteAsync(crop, MediaFileCategories.FaceCrops, subPath, cancellationToken);
                cropsRemoved++;
            }
        }

        return new TemplatePurgeResult(targets.Count, templatesRemoved, cropsRemoved);
    }
}
