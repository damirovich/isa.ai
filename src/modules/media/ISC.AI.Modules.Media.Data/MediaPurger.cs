using System.Globalization;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Corpus;
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
/// Порядок FAIL-CLOSED (как у <c>DocumentPurger</c> ядра): запись в неизменяемый аудит идёт ДО удаления
/// строк — недоступен журнал — исключение, удаление не выполняется (нет уничтожения биометрии без записи).
/// Удаление строк — одна транзакция БД (частичного результата нет; у <see cref="PurgeAsync"/> она же держит
/// блокировку носителя с момента подсчёта), и только ПОСЛЕ её фиксации — файлы:
/// осиротевший файл без строки безвреден (недостижим через API, подбирается уборкой), тогда как
/// строка без файла или файл при откате транзакции — дефект.
/// <para>Текст расшифровки в корпусе ядра (ADR-0051) — тоже производная носителя: он уничтожается
/// <paramref name="corpusPurger"/> своим актом ДО строк носителя (иначе мостик уйдёт каскадом и документ корпуса
/// станет недостижим). Порт ядра регистрирует любой хост; без него (тесты пакета) корпус не трогается.</para>
/// </remarks>
/// <param name="contextFactory">Фабрика контекста схемы <c>media</c>.</param>
/// <param name="auditWriter">Журнал аудита.</param>
/// <param name="fileStorage">Файловое хранилище.</param>
/// <param name="corpusPurger">Гарантированное удаление документа корпуса ядра.</param>
public sealed class MediaPurger(
    IDbContextFactory<MediaDbContext> contextFactory,
    IAuditWriter auditWriter,
    IFileStorage fileStorage,
    IDocumentPurger? corpusPurger = null) : IMediaPurger
{
    /// <inheritdoc />
    /// <remarks>
    /// АКТ СОВПАДАЕТ С УНИЧТОЖЕННЫМ (ТБ-064). Транзакция открывается ДО подсчёта, и первым делом берётся
    /// блокировка строки носителя (<c>FOR UPDATE</c>) — та же, что у записи расшифровки и результата индексации
    /// лиц. Пока она держится, ни фрагменты, ни лица этого носителя зафиксировать нельзя: числа в акте равны
    /// тому, что снесёт каскад. Без блокировки расшифровка, зафиксированная между подсчётом и удалением, ушла бы
    /// каскадом, а акт назвал бы прежнее число. Журнал пишется своим контекстом ядра под этой блокировкой:
    /// FAIL-CLOSED сохраняется — исключение журнала откатывает транзакцию, и удаления нет.
    /// </remarks>
    public async Task<MediaPurgeResult> PurgeAsync(
        int assetId, int? subjectId = null, CancellationToken cancellationToken = default)
    {
        // Обработанные копии фото (ТФ-МЕД-14, ТНД-008) уничтожаются вместе с источником — каждая своим актом и ДО него:
        // сбой на копии оставляет источник целым, и повтор удаления дочищает остальное. Снимки кадров (ADR-0028) —
        // не копии, а самостоятельные носители, их удаление источника не трогает.
        foreach (var copyId in await ListEditedCopiesAsync(assetId, cancellationToken))
        {
            await PurgeAsync(copyId, subjectId, cancellationToken);
        }

        // Расшифровка в корпусе ядра (ADR-0051) — до строк носителя, своим актом (fail-closed: журнал недоступен —
        // исключение, носитель не тронут).
        if (corpusPurger is not null)
        {
            await using var lookup = await contextFactory.CreateDbContextAsync(cancellationToken);
            var coreDocumentId = await lookup.TranscriptIndexLinks.AsNoTracking()
                .Where(l => l.AssetId == assetId)
                .Select(l => (int?)l.CoreDocumentId)
                .FirstOrDefaultAsync(cancellationToken);
            if (coreDocumentId is { } corpusDocument)
            {
                await corpusPurger.PurgeAsync(corpusDocument, subjectId, cancellationToken);
            }
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Блокировка строки носителя до конца транзакции (см. remarks). Идентификатор — параметром.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM media.asset WHERE id = {assetId} FOR UPDATE", cancellationToken);

        var asset = await db.Assets
            .Where(a => a.Id == assetId)
            .Select(a => new { a.Classification, a.DivisionId, a.StoredFileName, a.FilmstripStoredFileName })
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

        // Расшифровка речи (ADR-0026) и пометки (ADR-0050) уходят тем же каскадом; их объём — в акт, как и число лиц.
        var segmentCount = await db.TranscriptSegments.CountAsync(s => s.AssetId == assetId, cancellationToken);
        var annotationCount = await db.Annotations.CountAsync(a => a.AssetId == assetId, cancellationToken);

        // FAIL-CLOSED (ТБ-064): аудит ДО уничтожения данных. Недоступен журнал — WriteAsync бросит, транзакция
        // откатится при выходе и удаление НЕ произойдёт.
        await auditWriter.WriteAsync(
            new AuditEntry(
                AuditAction.Purge,
                asset.Classification,
                SubjectId: subjectId,
                ObjectRef: "media:asset:" + assetId.ToString(CultureInfo.InvariantCulture),
                DivisionId: asset.DivisionId,
                PayloadSensitive:
                    $"Гарантированное удаление носителя и биометрических производных: лиц {faceCount}, вырезок {crops.Count}, "
                    + $"фрагментов расшифровки {segmentCount}"
                    + (annotationCount == 0 ? string.Empty : $", пометок {annotationCount}")
                    + (asset.FilmstripStoredFileName is null ? string.Empty : ", ленты кадров")
                    + " (ТБ-064/075, ADR-0026, ADR-0038, ADR-0050)."),
            cancellationToken);

        // Атомарно и в той же транзакции под блокировкой: носитель → кадры/лица/шаблоны/фрагменты расшифровки
        // каскадом БД (см. конфигурации).
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

        // Лента кадров видео (ADR-0038) — уменьшенные кадры носителя: уничтожается вместе с ним (ТБ-064/075).
        if (asset.FilmstripStoredFileName is { } filmstrip)
        {
            await fileStorage.DeleteAsync(filmstrip, MediaFileCategories.Filmstrips, subPath, cancellationToken);
            filesRemoved++;
        }

        return new MediaPurgeResult(Found: true, FacesRemoved: faceCount, FilesRemoved: filesRemoved);
    }

    // Обработанные копии носителя: производные с набором операций и ссылкой на него (ADR-0050).
    private async Task<IReadOnlyList<int>> ListEditedCopiesAsync(int assetId, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Assets.AsNoTracking()
            .Where(a => a.SourceAssetId == assetId && a.EditOperations != null && a.Id != assetId)
            .Select(a => a.Id)
            .ToListAsync(cancellationToken);
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

    /// <inheritdoc />
    public async Task<CaseSearchPurgeResult> PurgeCaseSearchesAsync(
        int caseRef,
        string reason,
        int? subjectId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Состав — ДО удаления: после каскада ни чисел, ни имён файлов проб уже не взять.
        var sessions = await db.SearchSessions
            .Where(s => s.CaseId == caseRef)
            .Select(s => new { s.Id, s.Classification, s.DivisionId, s.ProbeCropStoredFileName })
            .ToListAsync(cancellationToken);

        if (sessions.Count == 0)
        {
            // Журнал сверок дела (ТФ-ПЕР-09) биометрии не несёт, но без дела ему не место — уходит и без истории поисков.
            await db.SuggestionRuns.Where(r => r.CaseId == caseRef).ExecuteDeleteAsync(cancellationToken);
            return CaseSearchPurgeResult.Empty; // идемпотентно: истории нет — ни удаления, ни записи
        }

        var sessionIds = sessions.Select(s => s.Id).ToList();
        var candidates = await db.SearchCandidates.CountAsync(c => sessionIds.Contains(c.SessionId), cancellationToken);
        var probeFiles = sessions
            .Where(s => s.ProbeCropStoredFileName is not null)
            .Select(s => s.ProbeCropStoredFileName!)
            .ToList();

        // FAIL-CLOSED (ТБ-064): аудит ДО уничтожения. Гриф записи — наивысший среди сессий дела: журнал
        // не усредняет гриф, а запись о сессиях не должна стать доступнее самих сессий (ТБ-032).
        var top = sessions.MaxBy(s => s.Classification)!;
        await auditWriter.WriteAsync(
            new AuditEntry(
                AuditAction.Purge,
                top.Classification,
                SubjectId: subjectId,
                ObjectRef: "media:case:" + caseRef.ToString(CultureInfo.InvariantCulture) + ":searches",
                DivisionId: top.DivisionId,
                PayloadSensitive:
                    $"Удаление истории поисков дела ({reason}): сессий {sessions.Count}, кандидатов {candidates}, "
                    + $"вырезок проб {probeFiles.Count}. Кандидаты в сессиях ДРУГИХ дел сохранены (ТБ-072, ADR-0025)."),
            cancellationToken);

        // Атомарно: сессии → кандидаты → решения верификации каскадом БД (см. конфигурации).
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.SearchSessions.Where(s => s.CaseId == caseRef).ExecuteDeleteAsync(cancellationToken);
        await db.SuggestionRuns.Where(r => r.CaseId == caseRef).ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        // Файлы проб — после фиксации; подкаталог = идентификатор дела (конвенция резолвера раздачи).
        var subPath = caseRef.ToString(CultureInfo.InvariantCulture);
        foreach (var file in probeFiles)
        {
            await fileStorage.DeleteAsync(file, MediaFileCategories.Probes, subPath, cancellationToken);
        }

        return new CaseSearchPurgeResult(sessions.Count, candidates, probeFiles.Count);
    }
}
