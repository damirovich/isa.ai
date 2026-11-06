using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using ISC.AI.Abstractions.Exchange;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using ISC.AI.Profile.Investigation.Domain.Entities;
using ISC.AI.Profile.Investigation.Domain.Exchange;
using ISC.AI.Profile.Investigation.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Profile.Investigation.Data.Exchange;

/// <summary>Что собрать: отбор, период в UTC, кто запустил.</summary>
/// <param name="Request">Отбор Администратора.</param>
/// <param name="FromUtc">Начало периода (включительно), UTC.</param>
/// <param name="ToUtc">Конец периода (не включительно), UTC.</param>
/// <param name="UserId">Кто запустил.</param>
/// <param name="CreatedAt">Время формирования — им же назван каталог пакета.</param>
public sealed record ExchangePackageJob(ExchangeExportRequest Request, DateTime FromUtc, DateTime ToUtc, int UserId, DateTimeOffset CreatedAt)
{
    /// <summary>Период месяца по местному времени сервера → полуинтервал UTC [1-е число; 1-е число следующего месяца).</summary>
    public static (DateTime FromUtc, DateTime ToUtc) MonthUtc(int year, int month, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var first = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Unspecified);
        return (TimeZoneInfo.ConvertTimeToUtc(first, zone), TimeZoneInfo.ConvertTimeToUtc(first.AddMonths(1), zone));
    }
}

/// <summary>Итог сборки пакета.</summary>
/// <param name="Package">Готовый пакет: имя каталога, объём, отпечатки ключей, SHA-256 манифеста (их записало ядро).</param>
/// <param name="Content">Описание пакета в манифесте: период, отбор, счётчики, маркировка.</param>
/// <param name="ShippedAssetIds">Носители, чьи файлы ушли этим пакетом.</param>
public sealed record ExchangePackageResult(
    ExchangePackageSealed Package, ExchangeManifestContent Content, IReadOnlyList<int> ShippedAssetIds)
{
    /// <summary>Имя каталога пакета на носителе.</summary>
    public string FolderName => Package.FolderName;
}

/// <summary>
/// Сборщик пакета обмена между офисами (ТФ-ОБМ-01, ТО-инф-14, ADR-0068): отбирает дела с движением за месяц (или по
/// номерам), собирает их состояние и движение за период, шифрует содержимое и файлы носителей ключом получателя, подписывает
/// манифест ключом этого экземпляра.
/// </summary>
/// <remarks>
/// ИНВАРИАНТЫ. (1) Шаблонов и векторов лиц в пакете нет (ТБ-076): лица — только кадр, таймкод и рамка
/// (<see cref="IMediaExchangeSource"/>). (2) Решётка допуска не применяется (ТБ-079, ТБ-083): пакет переносит данные офиса,
/// а Администратор, запустивший сборку, содержимого не видит — оно зашифровано для получателя. Поэтому сборщик вызывает только
/// фоновое задание под правом «Обмен между офисами». (3) Пакет либо готов целиком, либо его нет: каталог «.partial»
/// переименовывается последним шагом. (4) Шифрование, подпись, отпечатки ключей и список файлов манифеста — ядра
/// (<see cref="IExchangePackages"/>, ТБ-083): сборщик даёт только содержимое и описание отбора.
/// </remarks>
public sealed class ExchangePackageWriter(
    IDbContextFactory<InvestigationDbContext> contextFactory,
    IMediaExchangeSource media,
    IUserAccountStore users,
    IExchangePackages packages)
{
    /// <summary>Начало имени каталога пакета.</summary>
    public const string FolderPrefix = "isc-exchange-";

    /// <summary>Собирает пакет на носитель <paramref name="targetFolder"/>.</summary>
    public async Task<ExchangePackageResult> WriteAsync(
        ExchangePackageJob job,
        string instanceId,
        string targetFolder,
        RSA recipientKey,
        RSA signingKey,
        Action<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetFolder);
        ArgumentNullException.ThrowIfNull(recipientKey);
        ArgumentNullException.ThrowIfNull(signingKey);

        var collected = await CollectAsync(job, instanceId, cancellationToken);
        var folderName = FolderNameFor(instanceId, job.Request.Year, job.Request.Month, job.CreatedAt);

        await using var package = packages.Create(targetFolder, folderName, recipientKey);

        // Файлы носителей — по одному, имена порядковые: имя файла в пакете ничего не говорит о содержимом.
        var files = new Dictionary<int, string>();
        var number = 0;
        foreach (var assetId in collected.FileAssetIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = ExchangePackageData.MediaFolder + "/" + (++number).ToString("D6", CultureInfo.InvariantCulture) + ".enc";
            await using (var original = await media.OpenOriginalAsync(assetId, cancellationToken))
            {
                await package.AddAsync(name, original, progress, cancellationToken);
            }

            files[assetId] = name;
        }

        var data = collected.Data with
        {
            Assets = collected.Data.Assets.Select(a => a with { File = files.GetValueOrDefault(a.SourceId) }).ToList(),
        };

        using (var json = new MemoryStream())
        {
            await JsonSerializer.SerializeAsync(json, data, ExchangePackageData.Json, cancellationToken);
            json.Position = 0;
            await package.AddAsync(ExchangePackageData.FileName, json, progress, cancellationToken);
        }

        var classification = collected.Classification;
        var content = new ExchangeManifestContent(
            ExchangePackageData.Kind,
            SystemVersion(),
            instanceId,
            Period(job.Request.Year, job.Request.Month),
            job.CreatedAt,
            job.UserId,
            classification,
            ClassificationLevels.Label(classification),
            Selection(job.Request),
            data.Counts());
        var sealedPackage = await package.CompleteAsync(content, signingKey, cancellationToken);

        return new ExchangePackageResult(sealedPackage, content, [.. files.Keys]);
    }

    /// <summary>Имя каталога пакета: экземпляр, период и время формирования.</summary>
    public static string FolderNameFor(string instanceId, int year, int month, DateTimeOffset createdAt) =>
        FolderPrefix + instanceId + "-" + Period(year, month) + "-"
        + createdAt.ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture);

    /// <summary>Период «гггг-ММ».</summary>
    public static string Period(int year, int month) =>
        year.ToString("D4", CultureInfo.InvariantCulture) + "-" + month.ToString("D2", CultureInfo.InvariantCulture);

    private static Dictionary<string, string> Selection(ExchangeExportRequest request) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["period"] = Period(request.Year, request.Month),
            ["caseNumbers"] = string.Join(", ", request.CaseNumbers),
            ["mediaKinds"] = string.Join(", ", request.MediaKinds),
            ["resendFiles"] = request.ResendFiles ? "да" : "нет",
        };

    private static string SystemVersion() =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
        ?? "unknown";

    /// <summary>Собранное содержимое: данные пакета, носители с файлами, маркировка.</summary>
    internal sealed record Collected(ExchangePackageData Data, IReadOnlyList<int> FileAssetIds, short Classification);

    /// <summary>
    /// Отбор и сборка содержимого — без записи на носитель (её же проверяют тесты): дела, объекты, события связи, сводки,
    /// носители, лица, появления, справочники и сотрудники, на которые они ссылаются.
    /// </summary>
    internal async Task<Collected> CollectAsync(ExchangePackageJob job, string instanceId, CancellationToken cancellationToken)
    {
        var (from, to) = (job.FromUtc, job.ToUtc);
        var request = job.Request;
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        // --- Дела: по номерам, а без номеров — с движением за период.
        var casesQuery = db.Cases.AsNoTracking();
        if (request.CaseNumbers.Count > 0)
        {
            var numbers = request.CaseNumbers.ToList();
            casesQuery = casesQuery.Where(c => numbers.Contains(c.Number));
        }
        else
        {
            // Движение объекта — и его реквизиты, и события связи с его участием (ADR-0068 v0.4): изменённый телефон или
            // событие без правки самого объекта иначе не дошли бы до получателя.
            casesQuery = casesQuery.Where(c =>
                (c.CreatedAt >= from && c.CreatedAt < to) || (c.UpdatedAt >= from && c.UpdatedAt < to)
                || db.Persons.Any(p => p.CaseId == c.Id && (
                    (p.CreatedAt >= from && p.CreatedAt < to) || (p.UpdatedAt >= from && p.UpdatedAt < to)
                    || db.PersonAddresses.Any(a => a.PersonId == p.Id && ((a.CreatedAt >= from && a.CreatedAt < to) || (a.UpdatedAt >= from && a.UpdatedAt < to)))
                    || db.PersonPhones.Any(f => f.PersonId == p.Id && ((f.CreatedAt >= from && f.CreatedAt < to) || (f.UpdatedAt >= from && f.UpdatedAt < to)))
                    || db.PersonVehicles.Any(v => v.PersonId == p.Id && ((v.CreatedAt >= from && v.CreatedAt < to) || (v.UpdatedAt >= from && v.UpdatedAt < to)))
                    || db.LinkEventParticipants.Any(lp => lp.PersonId == p.Id && (
                        (lp.CreatedAt >= from && lp.CreatedAt < to) || (lp.UpdatedAt >= from && lp.UpdatedAt < to)
                        || db.LinkEvents.Any(e => e.Id == lp.EventId && ((e.CreatedAt >= from && e.CreatedAt < to) || (e.UpdatedAt >= from && e.UpdatedAt < to)))))))
                || db.CaseMediaLinks.Any(l => l.CaseId == c.Id && l.CreatedAt >= from && l.CreatedAt < to)
                || db.CaseReports.Any(r => r.CaseId == c.Id && ((r.CreatedAt >= from && r.CreatedAt < to) || (r.UpdatedAt >= from && r.UpdatedAt < to)))
                || db.Appearances.Any(a => a.CaseId == c.Id
                    && ((a.ConfirmedAtUtc >= from && a.ConfirmedAtUtc < to) || (a.RevokedAtUtc >= from && a.RevokedAtUtc < to))));
        }

        var cases = await casesQuery.OrderBy(c => c.Id).ToListAsync(cancellationToken);
        var caseIds = cases.Select(c => c.Id).ToList();

        // --- Объекты дел — текущее состояние с реквизитами.
        var persons = await db.Persons.AsNoTracking().Where(p => caseIds.Contains(p.CaseId)).OrderBy(p => p.Id).ToListAsync(cancellationToken);
        var personIds = persons.Select(p => p.Id).ToList();
        var addresses = (await db.PersonAddresses.AsNoTracking().Where(a => personIds.Contains(a.PersonId)).OrderBy(a => a.Id).ToListAsync(cancellationToken))
            .ToLookup(a => a.PersonId);
        var phones = (await db.PersonPhones.AsNoTracking().Where(p => personIds.Contains(p.PersonId)).OrderBy(p => p.Id).ToListAsync(cancellationToken))
            .ToLookup(p => p.PersonId);
        var vehicles = (await db.PersonVehicles.AsNoTracking().Where(v => personIds.Contains(v.PersonId)).OrderBy(v => v.Id).ToListAsync(cancellationToken))
            .ToLookup(v => v.PersonId);
        var referencePhotos = await db.ReferencePhotos.AsNoTracking().Where(r => personIds.Contains(r.PersonId)).OrderBy(r => r.Id).ToListAsync(cancellationToken);

        // --- События связи — те, где участвует объект пакета; участники — только объекты пакета (других у получателя не будет).
        var personSet = personIds.ToHashSet();
        var participants = await db.LinkEventParticipants.AsNoTracking().Where(p => personIds.Contains(p.PersonId)).ToListAsync(cancellationToken);
        var eventIds = participants.Select(p => p.EventId).Distinct().ToList();
        var events = await db.LinkEvents.AsNoTracking().Where(e => eventIds.Contains(e.Id)).OrderBy(e => e.Id).ToListAsync(cancellationToken);
        var participantsByEvent = participants.Where(p => personSet.Contains(p.PersonId)).ToLookup(p => p.EventId);

        // --- Сводки, созданные или изменённые за период, — действующая редакция.
        var reports = await db.CaseReports.AsNoTracking()
            .Where(r => caseIds.Contains(r.CaseId) && ((r.CreatedAt >= from && r.CreatedAt < to) || (r.UpdatedAt >= from && r.UpdatedAt < to)))
            .OrderBy(r => r.Id)
            .ToListAsync(cancellationToken);
        var reportIds = reports.Select(r => r.Id).ToList();
        var revisions = (await db.CaseReportRevisions.AsNoTracking().Where(v => reportIds.Contains(v.ReportId)).ToListAsync(cancellationToken))
            .ToLookup(v => v.ReportId);

        // --- Появления, подтверждённые или отозванные за период. Только свои: появление, перенесённое из другого офиса
        // (ADR-0068 v0.3), дальше не пересылается — у него нет своих решений верификации (ТБ-073), а обратного потока нет.
        // У своих сеанс, кандидат и первая подпись заданы всегда — это держит ограничение ck_appearance_origin.
        var appearances = await db.Appearances.AsNoTracking()
            .Where(a => caseIds.Contains(a.CaseId) && personIds.Contains(a.PersonId) && a.SourceInstance == null
                && ((a.ConfirmedAtUtc >= from && a.ConfirmedAtUtc < to) || (a.RevokedAtUtc >= from && a.RevokedAtUtc < to)))
            .OrderBy(a => a.Id)
            .ToListAsync(cancellationToken);

        // --- Носители: привязанные к делам пакета за период и те, на которые ссылаются эталоны и появления пакета (привязанные
        // и раньше): реквизиты — всегда, файл — по правилу ниже. Без файла получатель не посчитает лицо эталона своей моделью.
        var links = await db.CaseMediaLinks.AsNoTracking().Where(l => caseIds.Contains(l.CaseId)).ToListAsync(cancellationToken);
        var linkedInPeriod = links.Where(l => l.CreatedAt >= from && l.CreatedAt < to).Select(l => l.MediaAssetId).ToHashSet();
        var assetIds = linkedInPeriod
            .Concat(referencePhotos.Select(r => r.MediaAssetId))
            .Concat(appearances.Select(a => a.MediaAssetId))
            .ToHashSet();
        var assets = await media.GetAssetsAsync([.. assetIds], cancellationToken);

        // Файл едет, если вид носителя выбран и файл ещё не уходил успешным пакетом (или выбран повтор файлов).
        var kinds = request.MediaKinds.ToHashSet(StringComparer.Ordinal);
        var shipped = request.ResendFiles
            ? []
            : (await db.ExchangeExportFiles.AsNoTracking().Where(f => assetIds.Contains(f.AssetId)).Select(f => f.AssetId).Distinct()
                .ToListAsync(cancellationToken)).ToHashSet();
        var fileAssetIds = assets
            .Where(a => kinds.Contains(a.Kind.ToString()) && !shipped.Contains(a.AssetId))
            .Select(a => a.AssetId)
            .ToList();

        // --- Лица — на которые ссылаются эталоны и появления (кадр, таймкод, рамка).
        var faceIds = referencePhotos.Where(r => r.MediaFaceId is not null).Select(r => r.MediaFaceId!.Value)
            .Concat(appearances.Select(a => a.MediaFaceId))
            .ToHashSet();
        var faces = await media.GetFacesAsync([.. faceIds], cancellationToken);
        var decisions = (await media.GetDecisionsAsync([.. appearances.Select(a => a.CandidateId!.Value)], cancellationToken))
            .ToLookup(d => d.CandidateId);

        // --- Модель пакета.
        var exchangeCases = cases.Select(c => new ExchangeCase(
            c.Id, c.Number, c.Title, c.Kind, c.OpenedAt, c.Status, c.ClosedAt, c.Basis, c.DivisionId, c.Classification,
            c.InvestigatorUserId, c.CreatedByUserId,
            c.TaskNumber is null && c.InitiatorUnitId is null
                ? null
                : new ExchangeTask(c.TaskNumber, c.InitiatorUnitId, c.InitiatorName, c.InitiatorRankId, c.InitiatorPositionId,
                    c.InitiatorPhone, c.InitiatorDetails, c.Justification, c.Purpose, c.TaskNotes),
            c.CreatedAt, c.UpdatedAt)).ToList();

        var exchangePersons = persons.Select(p => new ExchangePerson(
            p.Id, p.CaseId, p.DisplayName, p.IsUnidentified, p.UnidentifiedNumber, p.Role, p.RoleInCase, p.BirthDate, p.BirthYear,
            p.BirthPlace, p.WorkPlace, p.Residence, p.Sex, p.Alias, p.Citizenship, p.Orientation, p.Notes, p.LinkedToPersonId,
            p.LinkTypeId, p.Classification, p.DivisionId,
            [.. addresses[p.Id].Select(a => new ExchangeAddress(a.Id, a.Kind, a.Text, a.Notes, a.Classification))],
            [.. phones[p.Id].Select(f => new ExchangePhone(f.Id, f.Kind, f.Number, f.Notes, f.Classification))],
            [.. vehicles[p.Id].Select(v => new ExchangeVehicle(v.Id, v.PlateNumber, v.Make, v.Model, v.Color, v.Notes, v.Classification))]))
            .ToList();

        var exchangeReferencePhotos = referencePhotos.Select(r => new ExchangeReferencePhoto(
            r.Id, r.PersonId, r.MediaAssetId, r.MediaFaceId, r.QualityScore, r.Source, r.LegalBasis, r.ReviewDueAt,
            r.AddedByUserId, r.SupersededById, r.Classification)).ToList();

        var exchangeEvents = events.Select(e => new ExchangeLinkEvent(
            e.Id, e.Kind, e.Title, e.OccurredOn, e.Description, e.CreatedByUserId, e.Classification, e.DivisionId,
            [.. participantsByEvent[e.Id].OrderBy(p => p.Id).Select(p => new ExchangeParticipant(
                p.Id, p.PersonId, p.Role, p.Level, p.InSearch, p.VehiclePlate, p.VehicleDetails, p.ContactAddress, p.Classification))]))
            .ToList();

        var exchangeReports = reports
            .Select(r => (Report: r, Revision: revisions[r.Id].FirstOrDefault(v => v.Number == r.CurrentRevision)))
            .Where(x => x.Revision is not null)
            .Select(x => new ExchangeReport(
                x.Report.Id, x.Report.CaseId, x.Report.PersonId, x.Report.Kind, x.Report.ReportDate, x.Report.IsActive,
                x.Report.CreatedByUserId, x.Report.CurrentRevision, x.Revision!.ContentJson, x.Revision.AuthorUserId,
                x.Revision.UpdatedAt ?? x.Revision.CreatedAt, x.Report.Classification, x.Report.DivisionId))
            .ToList();

        var linksByAsset = links.Where(l => assetIds.Contains(l.MediaAssetId)).ToLookup(l => l.MediaAssetId);
        var exchangeAssets = assets.Select(a => new ExchangeAsset(
            a.AssetId, a.Kind.ToString(), a.OriginalFileName, a.ContentType, a.ContentHash, a.ByteSize, a.DurationMs, a.Source,
            a.CapturedAt, a.RecordedAt, a.FrameRate, a.FrameWidth, a.FrameHeight, a.UploadedByUserId, a.SourceAssetId,
            a.SourceTimestampMs, a.EditOperations, a.Classification, a.DivisionId, a.CreatedAt, File: null,
            [.. linksByAsset[a.AssetId].OrderBy(l => l.CaseId)
                .Select(l => new ExchangeCaseMediaLink(l.CaseId, l.Place, l.LinkedByUserId, l.CreatedAt))]))
            .ToList();

        var exchangeFaces = faces.Select(f => new ExchangeFace(
            f.FaceId, f.AssetId, f.FrameIndex, f.TimestampMs, f.BoxX, f.BoxY, f.BoxWidth, f.BoxHeight)).ToList();

        var exchangeAppearances = appearances.Select(a => new ExchangeAppearance(
            a.Id, a.PersonId, a.CaseId, a.MediaAssetId, a.MediaFaceId, a.FrameIndex, a.FrameTimestampMs, a.Similarity, a.Status,
            a.ConfirmedAtUtc, a.ExpertUserId!.Value, a.VerifierUserId, a.RevokedAtUtc, a.RevokedByUserId, a.RevokeReason, a.Classification,
            [.. decisions[a.CandidateId!.Value].Select(d => new ExchangeDecision(
                d.Stage.ToString(), d.Verdict.ToString(), d.Rationale, d.DecidedAtUtc, d.UserId, d.ConfirmationMode.ToString()))]))
            .ToList();

        // --- Справочники, подразделения (с цепочкой вышестоящих — от них наследуется направление) и сотрудники.
        var referenceIds = cases.SelectMany(c => new[] { c.InitiatorUnitId, c.InitiatorRankId, c.InitiatorPositionId })
            .Concat(persons.Select(p => p.LinkTypeId))
            .Where(id => id is not null).Select(id => id!.Value).ToHashSet();
        var references = await db.ReferenceItems.AsNoTracking().Where(r => referenceIds.Contains(r.Id)).OrderBy(r => r.Id)
            .Select(r => new ExchangeReference(r.Id, r.Kind, r.Name, r.Code))
            .ToListAsync(cancellationToken);

        var divisionIds = cases.Select(c => c.DivisionId).Concat(persons.Select(p => p.DivisionId)).Concat(events.Select(e => e.DivisionId))
            .Concat(reports.Select(r => r.DivisionId)).Concat(assets.Select(a => a.DivisionId)).ToHashSet();
        var allDivisions = await db.Divisions.AsNoTracking().ToDictionaryAsync(d => d.Id, cancellationToken);
        foreach (var id in divisionIds.ToList())
        {
            for (var parent = allDivisions.GetValueOrDefault(id)?.ParentId; parent is { } p && divisionIds.Add(p);)
            {
                parent = allDivisions.GetValueOrDefault(p)?.ParentId;
            }
        }

        var divisions = divisionIds.Where(allDivisions.ContainsKey).Order()
            .Select(id => allDivisions[id])
            .Select(d => new ExchangeDivision(d.Id, d.Name, d.Code, d.Direction, d.ParentId))
            .ToList();

        var userIds = new HashSet<int>();
        void AddUser(int? id)
        {
            if (id is { } value)
            {
                userIds.Add(value);
            }
        }

        foreach (var c in cases) { AddUser(c.InvestigatorUserId); AddUser(c.CreatedByUserId); }
        foreach (var r in referencePhotos) { AddUser(r.AddedByUserId); }
        foreach (var e in events) { AddUser(e.CreatedByUserId); }
        foreach (var r in exchangeReports) { AddUser(r.CreatedByUserSourceId); AddUser(r.RevisionAuthorUserSourceId); }
        foreach (var a in assets) { AddUser(a.UploadedByUserId); }
        foreach (var l in exchangeAssets.SelectMany(a => a.Cases)) { AddUser(l.LinkedByUserSourceId); }
        foreach (var a in appearances) { AddUser(a.ExpertUserId); AddUser(a.VerifierUserId); AddUser(a.RevokedByUserId); }
        foreach (var d in exchangeAppearances.SelectMany(a => a.Decisions)) { AddUser(d.UserSourceId); }
        var accounts = await users.ListAsync(cancellationToken);
        var exchangeUsers = accounts.Where(u => userIds.Contains(u.UserId)).OrderBy(u => u.UserId)
            .Select(u => new ExchangeUser(u.UserId, u.UserName, u.DisplayName))
            .ToList();

        // Маркировка — наибольший гриф содержимого (ТБ-083).
        var classification = new[]
        {
            cases.Select(c => c.Classification), persons.Select(p => p.Classification), events.Select(e => e.Classification),
            reports.Select(r => r.Classification), assets.Select(a => a.Classification), appearances.Select(a => a.Classification),
            referencePhotos.Select(r => r.Classification),
        }.SelectMany(x => x).DefaultIfEmpty((short)0).Max();

        var data = new ExchangePackageData(
            ExchangePackageData.CurrentFormat, instanceId, request.Year, request.Month, job.CreatedAt,
            divisions, references, exchangeUsers, exchangeCases, exchangePersons, exchangeReferencePhotos, exchangeEvents,
            exchangeReports, exchangeAssets, exchangeFaces, exchangeAppearances);

        return new Collected(data, fileAssetIds, classification);
    }
}
