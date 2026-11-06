using System.Globalization;
using System.Text.Json;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using ISC.AI.Profile.Investigation.Domain.Entities;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Exchange;
using ISC.AI.Profile.Investigation.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ISC.AI.Profile.Investigation.Data.Exchange;

/// <summary>
/// Поиск того же лица в индексации получателя по месту в кадре источника (ТФ-ОБМ-02, ADR-0068 v0.3): кадр с тем же
/// таймкодом (в пределах допуска) и рамка с наибольшим перекрытием не ниже порога.
/// </summary>
/// <remarks>
/// ИНВАРИАНТ (ТБ-076): векторы и шаблоны не сравниваются — их в пакете нет. Файл получателя совпадает с файлом источника
/// по SHA-256, поэтому место лица в кадре определяет его однозначно, а детектор одной модели находит его там же.
/// </remarks>
public static class ExchangeFaceMatcher
{
    // Перекрытия, отличающиеся меньше этого, считаются равными — дальше решает близость кадра.
    private const double OverlapEpsilon = 1e-9;

    /// <summary>Перекрытие двух рамок — IoU: площадь пересечения к площади объединения, 0–1.</summary>
    public static double Overlap(float ax, float ay, float aw, float ah, float bx, float by, float bw, float bh)
    {
        if (aw <= 0 || ah <= 0 || bw <= 0 || bh <= 0)
        {
            return 0;
        }

        var width = Math.Min(ax + aw, bx + bw) - Math.Max(ax, bx);
        var height = Math.Min(ay + ah, by + bh) - Math.Max(ay, by);
        if (width <= 0 || height <= 0)
        {
            return 0;
        }

        var intersection = (double)width * height;
        var union = ((double)aw * ah) + ((double)bw * bh) - intersection;
        return union <= 0 ? 0 : intersection / union;
    }

    /// <summary>
    /// Лицо получателя на месте лица источника: кадр не дальше <paramref name="maxTimeOffsetMs"/>, перекрытие не ниже
    /// <paramref name="minOverlap"/>; из нескольких — с наибольшим перекрытием, затем с ближайшим кадром.
    /// <see langword="null"/> — не нашлось. У фото таймкод обоих — 0.
    /// </summary>
    public static MediaExchangeFace? Find(ExchangeFace source, IReadOnlyList<MediaExchangeFace> local, long maxTimeOffsetMs, double minOverlap)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(local);
        MediaExchangeFace? best = null;
        var bestOverlap = 0.0;
        var bestOffset = long.MaxValue;
        foreach (var face in local)
        {
            var offset = Math.Abs(face.TimestampMs - source.TimestampMs);
            if (offset > maxTimeOffsetMs)
            {
                continue;
            }

            var overlap = Overlap(source.BoxX, source.BoxY, source.BoxWidth, source.BoxHeight, face.BoxX, face.BoxY, face.BoxWidth, face.BoxHeight);
            if (overlap < minOverlap)
            {
                continue;
            }

            var better = best is null
                         || overlap > bestOverlap + OverlapEpsilon
                         || (Math.Abs(overlap - bestOverlap) <= OverlapEpsilon && (offset < bestOffset || (offset == bestOffset && face.FaceId < best.FaceId)));
            if (better)
            {
                best = face;
                bestOverlap = overlap;
                bestOffset = offset;
            }
        }

        return best;
    }
}

/// <summary>
/// Запись пакета, ждущая лица в индексации получателя (ADR-0068 v0.3): эталон или появление, лицо источника (кадр,
/// таймкод, рамка), дело источника и подписи сотрудников — то, что нужно, чтобы завести запись у получателя.
/// </summary>
/// <param name="ReferencePhoto">Эталон.</param>
/// <param name="Appearance">Появление.</param>
/// <param name="Face">Лицо источника; у эталона без выбранного лица — <see langword="null"/>.</param>
/// <param name="CaseSourceId">Дело источника (у эталона — дело его объекта).</param>
/// <param name="Users">Сотрудники источника, на которых ссылается запись.</param>
public sealed record ExchangePendingFacePayload(
    ExchangeReferencePhoto? ReferencePhoto,
    ExchangeAppearance? Appearance,
    ExchangeFace? Face,
    int? CaseSourceId,
    IReadOnlyList<ExchangeUser> Users)
{
    /// <summary>JSON для строки ожидания.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, ExchangePackageData.Json);

    /// <summary>Запись из строки ожидания; нечитаемая — <see langword="null"/>.</summary>
    public static ExchangePendingFacePayload? Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<ExchangePendingFacePayload>(json, ExchangePackageData.Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Объект источника.</summary>
    public int PersonSourceId => ReferencePhoto?.PersonSourceId ?? Appearance?.PersonSourceId ?? 0;

    /// <summary>Носитель источника.</summary>
    public int AssetSourceId => ReferencePhoto?.AssetSourceId ?? Appearance?.AssetSourceId ?? 0;

    /// <summary>Гриф записи источника.</summary>
    public short Classification => ReferencePhoto?.Classification ?? Appearance?.Classification ?? ClassificationLevels.Unclassified;
}

/// <summary>Итог прохода сопоставления: состояние каждой записи источника после прохода и сколько перенесено.</summary>
/// <param name="Statuses">Вид и номер записи источника → состояние.</param>
/// <param name="Linked">Перенесено записей.</param>
public sealed record ExchangeFaceLinkResult(
    IReadOnlyDictionary<(ExchangeRecordKind Kind, int SourceId), ExchangePendingFaceStatus> Statuses, int Linked);

/// <summary>Один проход сопоставления в каждый момент: приём и фоновая проверка не заведут одну запись дважды.</summary>
public sealed class ExchangeFaceLinkGate
{
    /// <summary>Замок прохода.</summary>
    public SemaphoreSlim Lock { get; } = new(1, 1);
}

/// <summary>
/// Перенос эталонов и появлений пакета обмена (ТФ-ОБМ-02, ADR-0068 v0.3): при приёме они ставятся в ожидание
/// (<see cref="ExchangePendingFace"/>), а заводятся, когда в индексации получателя нашлось то же лицо
/// (<see cref="ExchangeFaceMatcher"/>). Проход идёт в конце приёма и фоном (<see cref="ExchangeFaceLinkWorker"/>).
/// </summary>
/// <remarks>
/// <para>
/// ИНВАРИАНТ ТБ-073: появление получателя без кандидата заводится ТОЛЬКО здесь и только с пометкой источника и его
/// провенансом (<see cref="AppearanceProvenance"/>) — это решения источника, а не получателя; ограничение таблицы
/// <c>ck_appearance_origin</c> не даёт завести появление без кандидата иначе.
/// </para>
/// <para>
/// ИНВАРИАНТ ДОСТУПА (ТБ-079, ТБ-070): записи пишутся мимо допуска (системная операция приёма) с грифом источника, но не
/// ниже грифа носителя; подразделение — объекта (эталон) или дела (появление) у получателя. Запись и её строка карты —
/// одной транзакцией: повтор прохода дубля не заведёт (уникальный индекс карты).
/// </para>
/// </remarks>
public sealed class ExchangeFaceLinker(
    IDbContextFactory<InvestigationDbContext> contextFactory,
    IMediaExchangeSource media,
    IUserAccountStore accounts,
    IAuditWriter audit,
    ExchangeSettings settings,
    ExchangeFaceLinkGate gate,
    TimeProvider timeProvider)
{
    /// <summary>
    /// Ставит эталоны и появления пакета в ожидание. Уже перенесённые пропускаются; появление, отозванное у источника после
    /// переноса, отзывается и у получателя. Возвращает записи пакета, поставленные в ожидание.
    /// </summary>
    public async Task<IReadOnlySet<(ExchangeRecordKind Kind, int SourceId)>> QueueAsync(
        int importId, string packageName, string source, ExchangePackageData data, ExchangeImportTally tally, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(tally);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var mapped = await db.ExchangeSourceRecords.AsNoTracking()
            .Where(r => r.SourceInstance == source && (r.Kind == ExchangeRecordKind.ReferencePhoto || r.Kind == ExchangeRecordKind.Appearance))
            .ToDictionaryAsync(r => (r.Kind, r.SourceId), r => r.LocalId, cancellationToken);
        var existing = await db.ExchangePendingFaces
            .Where(r => r.SourceInstance == source)
            .ToDictionaryAsync(r => (r.Kind, r.SourceId), cancellationToken);
        var users = data.Users.ToDictionary(u => u.SourceId);
        var faces = data.Faces.ToDictionary(f => f.SourceId);
        var personCases = data.Persons.ToDictionary(p => p.SourceId, p => p.CaseSourceId);
        var queued = new HashSet<(ExchangeRecordKind, int)>();

        foreach (var photo in data.ReferencePhotos.OrderBy(p => p.SourceId))
        {
            if (mapped.ContainsKey((ExchangeRecordKind.ReferencePhoto, photo.SourceId)))
            {
                tally.AlreadyImported++;
                tally.Outcomes.Add(ExchangeOutcomes.ReferencePhotos, ExchangeOutcomes.Already);
                continue;
            }

            var payload = new ExchangePendingFacePayload(
                photo,
                null,
                photo.FaceSourceId is { } faceId && faces.TryGetValue(faceId, out var face) ? face : null,
                personCases.TryGetValue(photo.PersonSourceId, out var caseId) ? caseId : null,
                Signatures(users, [photo.AddedByUserSourceId]));
            Upsert(db, existing, importId, packageName, source, ExchangeRecordKind.ReferencePhoto, photo.SourceId, payload, now);
            queued.Add((ExchangeRecordKind.ReferencePhoto, photo.SourceId));
        }

        Dictionary<string, int>? localUsers = null;
        foreach (var appearance in data.Appearances.OrderBy(a => a.SourceId))
        {
            if (mapped.TryGetValue((ExchangeRecordKind.Appearance, appearance.SourceId), out var localId))
            {
                tally.AlreadyImported++;
                tally.Outcomes.Add(ExchangeOutcomes.Appearances, ExchangeOutcomes.Already);
                if (appearance.Status == AppearanceStatus.Revoked)
                {
                    // Отозвано у источника после переноса — отзывается и у получателя: ошибочное отождествление не должно
                    // оставаться действующим в другом офисе (ADR-0068 v0.3).
                    localUsers ??= await LocalUsersAsync(cancellationToken);
                    if (await RevokeAsync(db, localId, appearance, users, localUsers, now, cancellationToken))
                    {
                        tally.SourceRevocations.Add(localId);
                    }
                }

                continue;
            }

            var payload = new ExchangePendingFacePayload(
                null,
                appearance,
                faces.TryGetValue(appearance.FaceSourceId, out var face) ? face : null,
                appearance.CaseSourceId,
                Signatures(users, [appearance.ExpertUserSourceId, appearance.VerifierUserSourceId, appearance.RevokedByUserSourceId,
                    .. appearance.Decisions.Select(d => (int?)d.UserSourceId)]));
            Upsert(db, existing, importId, packageName, source, ExchangeRecordKind.Appearance, appearance.SourceId, payload, now);
            queued.Add((ExchangeRecordKind.Appearance, appearance.SourceId));
        }

        await db.SaveChangesAsync(cancellationToken);
        return queued;
    }

    /// <summary>
    /// Проход сопоставления: записи в ожидании и «лицо не найдено» после переиндексации носителя. <paramref name="source"/>
    /// — только этот источник (конец приёма), <see langword="null"/> — все (фон). <paramref name="auditLinks"/> — писать акт
    /// переноса в журнал аудита, если что-то перенесено (и при приёме: проход переносит и ждавшее из прежних пакетов).
    /// </summary>
    public async Task<ExchangeFaceLinkResult> LinkAsync(string? source, bool auditLinks, CancellationToken cancellationToken)
    {
        await gate.Lock.WaitAsync(cancellationToken);
        try
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var sources = await db.ExchangePendingFaces.AsNoTracking()
                .Where(r => (source == null || r.SourceInstance == source)
                            && (r.Status == ExchangePendingFaceStatus.Pending || r.Status == ExchangePendingFaceStatus.NotFound))
                .Select(r => r.SourceInstance)
                .Distinct()
                .ToListAsync(cancellationToken);
            var statuses = new Dictionary<(ExchangeRecordKind, int), ExchangePendingFaceStatus>();
            var linked = 0;
            Dictionary<string, int>? localUsers = null;
            foreach (var instance in sources.Order(StringComparer.Ordinal))
            {
                localUsers ??= await LocalUsersAsync(cancellationToken);
                var pass = await LinkSourceAsync(instance, localUsers, cancellationToken);
                linked += pass.Linked;
                if (source is not null)
                {
                    foreach (var (key, status) in pass.Statuses)
                    {
                        statuses[key] = status;
                    }
                }

                if (auditLinks && pass.Linked > 0)
                {
                    await audit.WriteAsync(
                        new AuditEntry(
                            AuditAction.Ingest,
                            pass.Classification,
                            SubjectId: null,
                            ObjectRef: "investigation:exchange:faces:" + instance,
                            PayloadSensitive: pass.Summary),
                        cancellationToken);
                }
            }

            return new ExchangeFaceLinkResult(statuses, linked);
        }
        finally
        {
            gate.Lock.Release();
        }
    }

    /// <summary>Есть ли записи, ждущие лица (фоновой проверке нечего делать, если нет).</summary>
    public async Task<bool> AnyWaitingAsync(CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.ExchangePendingFaces.AnyAsync(
            r => r.Status == ExchangePendingFaceStatus.Pending || r.Status == ExchangePendingFaceStatus.NotFound, cancellationToken);
    }

    // ---------- проход по одному источнику ----------

    private async Task<SourcePass> LinkSourceAsync(string source, Dictionary<string, int> localUsers, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.ExchangePendingFaces
            .Where(r => r.SourceInstance == source
                        && (r.Status == ExchangePendingFaceStatus.Pending || r.Status == ExchangePendingFaceStatus.NotFound))
            .OrderBy(r => r.Kind).ThenBy(r => r.SourceId)
            .ToListAsync(cancellationToken);
        var map = (await db.ExchangeSourceRecords.AsNoTracking()
                .Where(r => r.SourceInstance == source
                            && (r.Kind == ExchangeRecordKind.Case || r.Kind == ExchangeRecordKind.Person || r.Kind == ExchangeRecordKind.Asset))
                .Select(r => new { r.Kind, r.SourceId, r.LocalId })
                .ToListAsync(cancellationToken))
            .ToDictionary(r => (r.Kind, r.SourceId), r => r.LocalId);

        // Решения «пропустить» (и решённые, и применённые): записи пропущенного дела или носителя не переносятся.
        var skips = await db.ExchangeImportConflicts.AsNoTracking()
            .Where(c => c.SourceInstance == source && c.Decision == ExchangeConflictDecision.Skip
                        && (c.Status == ExchangeConflictStatus.Decided || c.Status == ExchangeConflictStatus.Applied))
            .Select(c => new { c.Kind, c.SourceId })
            .ToListAsync(cancellationToken);
        var skippedCases = skips.Where(s => s.Kind == ExchangeConflictKind.Case).Select(s => s.SourceId).ToHashSet();
        var skippedAssets = skips.Where(s => s.Kind == ExchangeConflictKind.Asset).Select(s => s.SourceId).ToHashSet();

        var payloads = rows.ToDictionary(r => r.Id, r => ExchangePendingFacePayload.Parse(r.Payload));
        var assetIds = payloads.Values.Where(p => p is not null && map.ContainsKey((ExchangeRecordKind.Asset, p.AssetSourceId)))
            .Select(p => map[(ExchangeRecordKind.Asset, p!.AssetSourceId)]).Distinct().ToList();
        var states = (await media.GetIndexStatesAsync(assetIds, cancellationToken)).ToDictionary(s => s.AssetId);
        var personIds = payloads.Values.Where(p => p is not null && map.ContainsKey((ExchangeRecordKind.Person, p.PersonSourceId)))
            .Select(p => map[(ExchangeRecordKind.Person, p!.PersonSourceId)]).Distinct().ToList();
        var personDivisions = await db.Persons.AsNoTracking().Where(p => personIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.DivisionId, cancellationToken);
        var caseIds = map.Where(m => m.Key.Kind == ExchangeRecordKind.Case).Select(m => m.Value).Distinct().ToList();
        var caseDivisions = await db.Cases.AsNoTracking().Where(c => caseIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.DivisionId, cancellationToken);

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var faces = new Dictionary<int, IReadOnlyList<MediaExchangeFace>>();
        var pass = new SourcePass();
        foreach (var row in rows)
        {
            var payload = payloads[row.Id];
            var outcome = payload is null
                ? await SaveAsync(db, row, ExchangePendingFaceStatus.NotFound, "запись пакета не читается", null, cancellationToken)
                : await TryLinkAsync(db, row, payload, source, map, skippedCases, skippedAssets, states, personDivisions, caseDivisions, faces,
                    localUsers, now, pass, cancellationToken);
            pass.Statuses[(row.Kind, row.SourceId)] = outcome;
        }

        await FixSupersededAsync(db, source, cancellationToken);
        return pass;
    }

    private async Task<ExchangePendingFaceStatus> TryLinkAsync(
        InvestigationDbContext db,
        ExchangePendingFace row,
        ExchangePendingFacePayload payload,
        string source,
        Dictionary<(ExchangeRecordKind Kind, int SourceId), int> map,
        HashSet<int> skippedCases,
        HashSet<int> skippedAssets,
        Dictionary<int, MediaExchangeIndexState> states,
        Dictionary<int, int> personDivisions,
        Dictionary<int, int> caseDivisions,
        Dictionary<int, IReadOnlyList<MediaExchangeFace>> faces,
        Dictionary<string, int> localUsers,
        DateTime now,
        SourcePass pass,
        CancellationToken cancellationToken)
    {
        // Что ещё не принято или пропущено по решению Руководителя — запись ждёт или не переносится.
        if (payload.CaseSourceId is { } caseSource && skippedCases.Contains(caseSource))
        {
            return await SaveAsync(db, row, ExchangePendingFaceStatus.Skipped, "дело пропущено по решению Руководителя", null, cancellationToken);
        }

        if (skippedAssets.Contains(payload.AssetSourceId))
        {
            return await SaveAsync(db, row, ExchangePendingFaceStatus.Skipped, "носитель пропущен по решению Руководителя", null, cancellationToken);
        }

        if (!map.TryGetValue((ExchangeRecordKind.Person, payload.PersonSourceId), out var personId))
        {
            return await SaveAsync(db, row, ExchangePendingFaceStatus.Pending, "ждёт объект: его дело ещё не принято (конфликт или не дошёл пакет)", null, cancellationToken);
        }

        if (!personDivisions.TryGetValue(personId, out var personDivision))
        {
            return await SaveAsync(db, row, ExchangePendingFaceStatus.Skipped, "объект у получателя удалён", null, cancellationToken);
        }

        var caseId = 0;
        var caseDivision = 0;
        if (payload.Appearance is { } appearanceSource
            && (!map.TryGetValue((ExchangeRecordKind.Case, appearanceSource.CaseSourceId), out caseId) || !caseDivisions.TryGetValue(caseId, out caseDivision)))
        {
            return await SaveAsync(db, row, ExchangePendingFaceStatus.Pending, "ждёт дело: оно ещё не принято", null, cancellationToken);
        }

        if (!map.TryGetValue((ExchangeRecordKind.Asset, payload.AssetSourceId), out var assetId))
        {
            return await SaveAsync(db, row, ExchangePendingFaceStatus.Pending, "ждёт носитель: он в конфликте или файл ещё не получен", null, cancellationToken);
        }

        if (!states.TryGetValue(assetId, out var state))
        {
            return await SaveAsync(db, row, ExchangePendingFaceStatus.Pending, "носитель у получателя не найден", null, cancellationToken);
        }

        MediaExchangeFace? match = null;
        if (payload.Face is { } face)
        {
            switch (state.Status)
            {
                case MediaIndexStatus.Indexed:
                    break;
                case MediaIndexStatus.Failed:
                    return await SaveAsync(db, row, ExchangePendingFaceStatus.Pending,
                        "индексация носителя не удалась — переиндексируйте его: " + (state.Error ?? "причина не указана"), null, cancellationToken);
                case MediaIndexStatus.NotApplicable:
                    return await SaveAsync(db, row, ExchangePendingFaceStatus.NotFound, "у носителя нет кадров для поиска лиц", state.IndexedAt, cancellationToken);
                default:
                    return await SaveAsync(db, row, ExchangePendingFaceStatus.Pending, "ждёт индексации носителя", null, cancellationToken);
            }

            // «Лицо не найдено» перепроверяется только по новой индексации: на прежней ответ тот же.
            if (row.Status == ExchangePendingFaceStatus.NotFound && row.CheckedIndexAt == state.IndexedAt)
            {
                return ExchangePendingFaceStatus.NotFound;
            }

            if (!faces.TryGetValue(assetId, out var localFaces))
            {
                faces[assetId] = localFaces = await media.ListAssetFacesAsync(assetId, cancellationToken);
            }

            match = ExchangeFaceMatcher.Find(face, localFaces, settings.FaceMatchMaxTimeOffsetMs, settings.FaceMatchMinOverlap);
            if (match is null)
            {
                return await SaveAsync(db, row, ExchangePendingFaceStatus.NotFound,
                    string.Create(CultureInfo.InvariantCulture,
                        $"на {Moment(face)} лица с перекрытием рамки не ниже {settings.FaceMatchMinOverlap * 100:0} % не найдено"),
                    state.IndexedAt, cancellationToken);
            }
        }
        else if (payload.Appearance is not null)
        {
            return await SaveAsync(db, row, ExchangePendingFaceStatus.NotFound, "лицо появления в пакете не передано", null, cancellationToken);
        }

        // Гриф — источника, но не ниже грифа носителя (ТБ-070): приём гриф не понижает.
        var classification = Math.Max(payload.Classification, state.Classification);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        int localId;
        if (payload.ReferencePhoto is { } photo)
        {
            var entity = new ReferencePhoto
            {
                PersonId = personId,
                MediaAssetId = assetId,
                MediaFaceId = match?.FaceId,
                QualityScore = photo.QualityScore,
                Source = photo.Source,
                LegalBasis = photo.LegalBasis,
                ReviewDueAt = photo.ReviewDueAt,
                AddedByUserId = LocalUser(payload, photo.AddedByUserSourceId, localUsers),
                Classification = classification,
                DivisionId = personDivision,
            };
            db.ReferencePhotos.Add(entity);
            await db.SaveChangesAsync(cancellationToken);
            localId = entity.Id;
            pass.ReferencePhotos++;
        }
        else
        {
            var appearance = payload.Appearance!;
            var entity = new Appearance
            {
                PersonId = personId,
                CaseId = caseId,
                MediaAssetId = assetId,
                MediaFaceId = match!.FaceId,

                // Кадр — получателя (у фото кадра нет), остальное — источника: это его подтверждение, не наше (ТБ-073).
                FrameIndex = appearance.FrameIndex is null ? null : match.FrameIndex,
                FrameTimestampMs = appearance.FrameTimestampMs is null ? null : match.TimestampMs,
                SearchSessionId = null,
                CandidateId = null,
                Similarity = appearance.Similarity,
                Status = appearance.Status,
                ConfirmedAtUtc = appearance.ConfirmedAtUtc,
                ExpertUserId = LocalUser(payload, appearance.ExpertUserSourceId, localUsers),
                VerifierUserId = LocalUser(payload, appearance.VerifierUserSourceId, localUsers),
                RevokedAtUtc = appearance.RevokedAtUtc,
                RevokedByUserId = LocalUser(payload, appearance.RevokedByUserSourceId, localUsers),
                RevokeReason = appearance.RevokeReason,
                Classification = classification,
                DivisionId = caseDivision,
                SourceInstance = source,
                SourceProvenance = Provenance(row, payload, appearance, source, now).ToJson(),
            };
            db.Appearances.Add(entity);
            await db.SaveChangesAsync(cancellationToken);
            localId = entity.Id;
            pass.Appearances++;
        }

        db.ExchangeSourceRecords.Add(new ExchangeSourceRecord
        {
            SourceInstance = source,
            Kind = row.Kind,
            SourceId = row.SourceId,
            LocalId = localId,
            ImportId = row.ImportId,
            CreatedAt = now,
        });
        row.Status = ExchangePendingFaceStatus.Linked;
        row.LocalId = localId;
        row.Reason = null;
        row.CheckedIndexAt = state.IndexedAt;
        row.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        pass.Linked++;
        pass.Classification = Math.Max(pass.Classification, classification);
        return ExchangePendingFaceStatus.Linked;
    }

    // Замена эталона у источника: прежний эталон получателя ссылается на принятый эталон-замену (ТБ-077).
    private static async Task FixSupersededAsync(InvestigationDbContext db, string source, CancellationToken cancellationToken)
    {
        var linked = await db.ExchangePendingFaces.AsNoTracking()
            .Where(r => r.SourceInstance == source && r.Kind == ExchangeRecordKind.ReferencePhoto && r.Status == ExchangePendingFaceStatus.Linked)
            .Select(r => new { r.LocalId, r.Payload })
            .ToListAsync(cancellationToken);
        var replacements = linked
            .Select(r => (r.LocalId, Payload: ExchangePendingFacePayload.Parse(r.Payload)))
            .Where(r => r.LocalId is not null && r.Payload?.ReferencePhoto?.SupersededBySourceId is not null)
            .Select(r => (LocalId: r.LocalId!.Value, By: r.Payload!.ReferencePhoto!.SupersededBySourceId!.Value))
            .ToList();
        if (replacements.Count == 0)
        {
            return;
        }

        var byIds = replacements.Select(r => r.By).ToList();
        var mapped = await db.ExchangeSourceRecords.AsNoTracking()
            .Where(r => r.SourceInstance == source && r.Kind == ExchangeRecordKind.ReferencePhoto && byIds.Contains(r.SourceId))
            .ToDictionaryAsync(r => r.SourceId, r => r.LocalId, cancellationToken);
        foreach (var (localId, by) in replacements)
        {
            if (mapped.TryGetValue(by, out var replacement))
            {
                await db.ReferencePhotos
                    .Where(p => p.Id == localId && p.SupersededById == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(p => p.SupersededById, replacement), cancellationToken);
            }
        }
    }

    // Отзыв у источника переносится на перенесённое появление; уже отозванное получателем не трогается.
    private static async Task<bool> RevokeAsync(
        InvestigationDbContext db,
        int appearanceId,
        ExchangeAppearance source,
        Dictionary<int, ExchangeUser> users,
        Dictionary<string, int> localUsers,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var appearance = await db.Appearances.FirstOrDefaultAsync(a => a.Id == appearanceId, cancellationToken);
        if (appearance is null || appearance.Status == AppearanceStatus.Revoked)
        {
            return false;
        }

        var revoker = source.RevokedByUserSourceId is { } by && users.TryGetValue(by, out var user) ? user : null;
        appearance.Status = AppearanceStatus.Revoked;
        appearance.RevokedAtUtc = source.RevokedAtUtc ?? now;
        appearance.RevokeReason = string.IsNullOrWhiteSpace(source.RevokeReason) ? "отозвано у источника" : source.RevokeReason.Trim();
        appearance.RevokedByUserId = revoker is not null && localUsers.TryGetValue(revoker.UserName.Trim(), out var local) ? local : null;
        if (AppearanceProvenance.Parse(appearance.SourceProvenance) is { } provenance && revoker is not null)
        {
            appearance.SourceProvenance = (provenance with { RevokedBy = new ProvenanceSignature(revoker.UserName, revoker.DisplayName) }).ToJson();
        }

        return true;
    }

    private static AppearanceProvenance Provenance(
        ExchangePendingFace row, ExchangePendingFacePayload payload, ExchangeAppearance appearance, string source, DateTime now)
    {
        var users = payload.Users.ToDictionary(u => u.SourceId);
        return new AppearanceProvenance(
            source,
            row.PackageName,
            appearance.SourceId,
            Signature(users, appearance.ExpertUserSourceId),
            Signature(users, appearance.VerifierUserSourceId),
            Signature(users, appearance.RevokedByUserSourceId),
            [.. appearance.Decisions.OrderBy(d => d.DecidedAtUtc).Select(d => new ProvenanceDecision(
                d.Stage, d.Verdict, d.Rationale, d.DecidedAtUtc, Signature(users, d.UserSourceId), d.ConfirmationMode))],
            now);
    }

    private static ProvenanceSignature? Signature(Dictionary<int, ExchangeUser> users, int? sourceId) =>
        sourceId is { } id && users.TryGetValue(id, out var user) ? new ProvenanceSignature(user.UserName, user.DisplayName) : null;

    // Учётка получателя с тем же именем входа — для правила «отзывает не тот, кто подтверждал»; иначе пусто.
    private static int? LocalUser(ExchangePendingFacePayload payload, int? sourceId, Dictionary<string, int> localUsers) =>
        sourceId is { } id && payload.Users.FirstOrDefault(u => u.SourceId == id) is { } user && localUsers.TryGetValue(user.UserName.Trim(), out var local)
            ? local
            : null;

    private async Task<Dictionary<string, int>> LocalUsersAsync(CancellationToken cancellationToken) =>
        (await accounts.ListAsync(cancellationToken))
            .GroupBy(a => a.UserName.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().UserId, StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyList<ExchangeUser> Signatures(Dictionary<int, ExchangeUser> users, IEnumerable<int?> ids) =>
        [.. ids.Where(id => id is not null).Select(id => id!.Value).Distinct().Where(users.ContainsKey).Select(id => users[id])];

    private static void Upsert(
        InvestigationDbContext db,
        Dictionary<(ExchangeRecordKind, int), ExchangePendingFace> existing,
        int importId,
        string packageName,
        string source,
        ExchangeRecordKind kind,
        int sourceId,
        ExchangePendingFacePayload payload,
        DateTime now)
    {
        if (!existing.TryGetValue((kind, sourceId), out var row))
        {
            row = new ExchangePendingFace
            {
                SourceInstance = source,
                Kind = kind,
                SourceId = sourceId,
                PackageName = packageName,
                Payload = string.Empty,
                CreatedAt = now,
            };
            db.ExchangePendingFaces.Add(row);
            existing[(kind, sourceId)] = row;
        }

        // Повторный приём освежает запись (у источника её могли отозвать) и снова отдаёт её проходу; перенесённую — не трогает.
        if (row.Status == ExchangePendingFaceStatus.Linked)
        {
            return;
        }

        row.ImportId = importId;
        row.PackageName = packageName.Length <= 200 ? packageName : packageName[..200];
        row.Payload = payload.ToJson();
        row.Status = ExchangePendingFaceStatus.Pending;
        row.Reason = "ждёт сопоставления лица";
        row.CheckedIndexAt = null;
        row.Classification = payload.Classification;
        row.UpdatedAt = now;
    }

    private async Task<ExchangePendingFaceStatus> SaveAsync(
        InvestigationDbContext db, ExchangePendingFace row, ExchangePendingFaceStatus status, string reason, DateTime? checkedIndexAt,
        CancellationToken cancellationToken)
    {
        if (row.Status != status || !string.Equals(row.Reason, reason, StringComparison.Ordinal) || row.CheckedIndexAt != checkedIndexAt)
        {
            row.Status = status;
            row.Reason = reason.Length <= 500 ? reason : reason[..500];
            row.CheckedIndexAt = checkedIndexAt;
            row.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(cancellationToken);
        }

        return status;
    }

    private static string Moment(ExchangeFace face)
    {
        if (face.FrameIndex == 0 && face.TimestampMs == 0)
        {
            return "изображении";
        }

        var time = TimeSpan.FromMilliseconds(face.TimestampMs);
        return "кадре " + time.ToString(time.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture);
    }

    private sealed class SourcePass
    {
        public Dictionary<(ExchangeRecordKind, int), ExchangePendingFaceStatus> Statuses { get; } = new();

        public int Linked { get; set; }

        public int ReferencePhotos { get; set; }

        public int Appearances { get; set; }

        public short Classification { get; set; }

        public string Summary => string.Create(
            CultureInfo.InvariantCulture,
            $"перенесено после приёма: эталонов {ReferencePhotos}, появлений {Appearances}; ждут {Statuses.Values.Count(s => s == ExchangePendingFaceStatus.Pending)}, "
            + $"лицо не найдено {Statuses.Values.Count(s => s == ExchangePendingFaceStatus.NotFound)}, пропущено {Statuses.Values.Count(s => s == ExchangePendingFaceStatus.Skipped)}");
    }
}

/// <summary>
/// Фоновое сопоставление лиц после приёма (ADR-0068 v0.3): носители, принятые пакетом, индексируются позже — раз в
/// <c>Exchange:Import:FaceMatch:PollSeconds</c> проход переносит эталоны и появления, лица которых уже нашлись.
/// </summary>
public sealed partial class ExchangeFaceLinkWorker(
    IServiceScopeFactory scopes, ExchangeSettings settings, ILogger<ExchangeFaceLinkWorker> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(settings.FaceMatchPollSeconds));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var linker = scope.ServiceProvider.GetRequiredService<ExchangeFaceLinker>();
                if (await linker.AnyWaitingAsync(stoppingToken))
                {
                    var result = await linker.LinkAsync(null, auditLinks: true, stoppingToken);
                    if (result.Linked > 0)
                    {
                        LogLinked(logger, result.Linked);
                    }
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Сбой прохода не роняет хост: записи остаются в ожидании, следующий проход повторит.
                LogFailed(logger, exception);
            }
        }
    }

    [LoggerMessage(EventId = 6821, Level = LogLevel.Information, Message = "Обмен: после приёма перенесено эталонов и появлений: {Count}")]
    private static partial void LogLinked(ILogger logger, int count);

    [LoggerMessage(EventId = 6822, Level = LogLevel.Warning, Message = "Обмен: проход сопоставления лиц не удался — повторится следующим")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
