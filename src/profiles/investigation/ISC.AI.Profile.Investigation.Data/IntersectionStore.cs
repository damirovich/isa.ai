using System.Globalization;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Domain.Services;
using ISC.AI.Profile.Investigation.Domain.Entities;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Profile.Investigation.Data;

/// <summary>
/// Пересечения между делами (ТФ-ПЕР-07, ТБ-084, ADR-0029) поверх нормализованных реквизитов (ТО-мат-11):
/// госномер, адрес (строки адресов и место жительства из анкеты), ФИО + дата рождения. Совпадения считаются
/// при каждом чтении точным сравнением ключей по индексам; хранится только решение человека
/// (<c>investigation.intersection_review</c>).
/// </summary>
/// <remarks>
/// ИНВАРИАНТ ВИДИМОСТИ (ТБ-084, ТБ-020/021, ADR-0029 п. 1–2):
/// <list type="bullet">
/// <item>исходный фигурант — по ПОЛНОЙ решётке (<see cref="PersonAccess"/>): недоступный неотличим от отсутствующего;</item>
/// <item>«чужая» сторона — дела, фигуранты и строки реквизитов, прошедшие floor ядра (<see cref="BaselineAccess"/>)
/// и политику профиля, БЕЗ сужения по роли (<see cref="CaseAccessRule.Narrow"/> не применяется): так следователь
/// видит совпадение с делом коллеги, но никогда — с делом выше допуска или чужого подразделения;</item>
/// <item>ответ одинаков по составу запросов при любом числе скрытых совпадений — скрытое не проявляется ни
/// счётчиком, ни отдельной ветвью (ТБ-021);</item>
/// <item>наружу — только поля, разрешённые ТБ-084 (номер, вид, статус дела, реквизит, ответственный);
/// несколько фигурантов чужого дела с одним ключом схлопываются в одну строку.</item>
/// </list>
/// </remarks>
public sealed class IntersectionStore(
    IDbContextFactory<InvestigationDbContext> contextFactory,
    IAccessPolicy policy,
    IUserRoleStore roles,
    IMediaCatalog mediaCatalog) : IIntersectionStore
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<IntersectionRow>?> FindForPersonAsync(
        int personId, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);

        var role = await ResolveRoleAsync(access, cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var found = await FindAsync(db, personId, access, role, cancellationToken);
        return found?.Rows;
    }

    /// <inheritdoc />
    public async Task<PersonWriteResult> ReviewAsync(
        int personId,
        IntersectionKind kind,
        string key,
        int otherCaseId,
        IntersectionDecision decision,
        AccessContext access,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var role = await ResolveRoleAsync(access, cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        // ИНВАРИАНТ (ADR-0029 п. 6): решение пишется только по пересечению, которое субъект СЕЙЧАС видит.
        // Иначе строка решения стала бы оракулом: «Ok» на произвольный номер чужого дела подтверждал бы его
        // существование и совпадение ключа (ТБ-084, ТБ-021).
        var found = await FindAsync(db, personId, access, role, cancellationToken);
        if (found is not { } visible
            || !visible.Rows.Any(r => r.Kind == kind && r.Key == key && r.OtherCaseId == otherCaseId))
        {
            return PersonWriteResult.NotFound;
        }

        var review = await db.IntersectionReviews.FirstOrDefaultAsync(
            r => r.PersonId == personId && r.Kind == kind && r.KeyNormalized == key && r.OtherCaseId == otherCaseId,
            cancellationToken);
        if (review is null)
        {
            review = new IntersectionReview
            {
                PersonId = personId,
                CaseId = visible.Own.CaseId,
                Kind = kind,
                KeyNormalized = key,
                OtherCaseId = otherCaseId,
                Classification = visible.Own.Classification,
                DivisionId = visible.Own.DivisionId,
            };
            db.IntersectionReviews.Add(review);
        }

        review.Decision = decision;
        review.DecidedByUserId = access.NumericSubjectId;
        review.DecidedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(cancellationToken);
        return PersonWriteResult.Ok;
    }

    private async Task<(OwnPerson Own, IReadOnlyList<IntersectionRow> Rows)?> FindAsync(
        InvestigationDbContext db, int personId, AccessContext access, InvestigationRole? role, CancellationToken cancellationToken)
    {
        // Своя сторона — по полной решётке (роль/владение через дело).
        var own = await PersonAccess.Accessible(db, access, policy, role)
            .Where(p => p.Id == personId)
            .Select(p => new OwnPerson(
                p.Id, p.CaseId, p.Classification, p.DivisionId, p.DisplayName, p.NameNormalized, p.BirthDate, p.ResidenceNormalized, p.Residence))
            .FirstOrDefaultAsync(cancellationToken);
        if (own is null)
        {
            return null;
        }

        // Свои ключи: значения собственных строк реквизитов (тоже под floor'ом — у строк свои режимные поля).
        var ownPlates = await db.PersonVehicles.AsNoTracking()
            .Where(BaselineAccess.Filter<PersonVehicle>(access))
            .Where(policy.BuildFilter<PersonVehicle>(access))
            .Where(v => v.PersonId == own.Id && v.PlateNormalized != null)
            .OrderBy(v => v.Id)
            .Select(v => new { Key = v.PlateNormalized!, Value = v.PlateNumber ?? v.PlateNormalized! })
            .ToListAsync(cancellationToken);

        var ownAddresses = await db.PersonAddresses.AsNoTracking()
            .Where(BaselineAccess.Filter<PersonAddress>(access))
            .Where(policy.BuildFilter<PersonAddress>(access))
            .Where(a => a.PersonId == own.Id)
            .OrderBy(a => a.Id)
            .Select(a => new { Key = a.TextNormalized, Value = a.Text })
            .ToListAsync(cancellationToken);
        if (!string.IsNullOrEmpty(own.ResidenceNormalized))
        {
            ownAddresses.Add(new { Key = own.ResidenceNormalized, Value = own.Residence ?? own.ResidenceNormalized });
        }

        var ownValues = new Dictionary<(IntersectionKind, string), string>();
        foreach (var plate in ownPlates) { ownValues.TryAdd((IntersectionKind.Vehicle, plate.Key), plate.Value); }
        foreach (var address in ownAddresses) { ownValues.TryAdd((IntersectionKind.Address, address.Key), address.Value); }

        var plateKeys = ownPlates.Select(p => p.Key).Distinct().ToList();
        var addressKeys = ownAddresses.Select(a => a.Key).Distinct().ToList();

        // «Чужая» сторона: floor ядра + политика профиля на КАЖДОЙ строке цепочки (дело → фигурант → реквизит),
        // без сужения по роли (ТБ-084). Своё дело исключено: совпадение внутри дела — не пересечение.
        var otherCases = db.Cases.AsNoTracking()
            .Where(BaselineAccess.Filter<CaseFile>(access))
            .Where(policy.BuildFilter<CaseFile>(access))
            .Where(c => c.Id != own.CaseId);
        var otherPersons = db.Persons.AsNoTracking()
            .Where(BaselineAccess.Filter<Person>(access))
            .Where(policy.BuildFilter<Person>(access))
            .Where(p => otherCases.Any(c => c.Id == p.CaseId));

        var matches = new List<Match>();

        var vehicleMatches = await db.PersonVehicles.AsNoTracking()
            .Where(BaselineAccess.Filter<PersonVehicle>(access))
            .Where(policy.BuildFilter<PersonVehicle>(access))
            .Where(v => v.PlateNormalized != null && plateKeys.Contains(v.PlateNormalized))
            .Join(otherPersons, v => v.PersonId, p => p.Id, (v, p) => new { Key = v.PlateNormalized!, Value = v.PlateNumber, p.CaseId })
            .ToListAsync(cancellationToken);
        matches.AddRange(vehicleMatches.Select(m => new Match(IntersectionKind.Vehicle, m.Key, m.Value ?? m.Key, m.CaseId)));

        var addressMatches = await db.PersonAddresses.AsNoTracking()
            .Where(BaselineAccess.Filter<PersonAddress>(access))
            .Where(policy.BuildFilter<PersonAddress>(access))
            .Where(a => addressKeys.Contains(a.TextNormalized))
            .Join(otherPersons, a => a.PersonId, p => p.Id, (a, p) => new { Key = a.TextNormalized, Value = a.Text, p.CaseId })
            .ToListAsync(cancellationToken);
        matches.AddRange(addressMatches.Select(m => new Match(IntersectionKind.Address, m.Key, m.Value, m.CaseId)));

        var residenceMatches = await otherPersons
            .Where(p => p.ResidenceNormalized != null && addressKeys.Contains(p.ResidenceNormalized))
            .Select(p => new { Key = p.ResidenceNormalized!, Value = p.Residence, p.CaseId })
            .ToListAsync(cancellationToken);
        matches.AddRange(residenceMatches.Select(m => new Match(IntersectionKind.Address, m.Key, m.Value ?? m.Key, m.CaseId)));

        // ФИО + дата рождения — только когда у своей стороны заданы оба (ADR-0029 п. 3): совпадение по одному
        // ФИО или по году рождения слишком часто случайно. Запрос выполняется всегда (с заведомо ложным условием
        // при пустом ключе) — состав запросов не зависит от данных (ТБ-021).
        var nameKey = own.NameNormalized is { Length: > 0 } && own.BirthDate is not null ? own.NameNormalized : null;
        var birthDate = own.BirthDate;
        var nameMatches = await otherPersons
            .Where(p => nameKey != null && p.NameNormalized == nameKey && p.BirthDate == birthDate)
            .Select(p => new { p.DisplayName, p.BirthDate, p.CaseId })
            .ToListAsync(cancellationToken);
        if (nameKey is not null)
        {
            var key = NameKey(nameKey, birthDate!.Value);
            ownValues.TryAdd((IntersectionKind.PersonName, key), NameValue(own.DisplayName, birthDate.Value));
            matches.AddRange(nameMatches.Select(m => new Match(IntersectionKind.PersonName, key, NameValue(m.DisplayName, m.BirthDate!.Value), m.CaseId)));
        }

        // Лицо (ТФ-ПЕР-07, ADR-0029 п. 3а) — ТОЛЬКО подтверждённые появления: «появление» записывается после двух
        // независимых «подтверждён» разных сотрудников (ТБ-073), сырые кандидаты поиска сюда не попадают никогда.
        // Своя сторона — появления объекта под floor'ом строки появления; счётчики строятся ТОЛЬКО по ним (их субъект
        // и так видит в карточке объекта), о чужих фигурантах наружу не идёт ни имя, ни число.
        var ownAppearances = await db.Appearances.AsNoTracking()
            .Where(BaselineAccess.Filter<Appearance>(access))
            .Where(policy.BuildFilter<Appearance>(access))
            .Where(a => a.PersonId == own.Id)
            .Select(a => new { a.MediaAssetId, a.MediaFaceId, a.ConfirmedAtUtc })
            .ToListAsync(cancellationToken);
        var ownAssetIds = ownAppearances.Select(a => a.MediaAssetId).Distinct().ToList();
        var ownFaceIds = ownAppearances.Select(a => a.MediaFaceId).Distinct().ToList();

        // Вид 1: объект подтверждён на материале, который есть и в ДРУГОМ деле. Материал — тот же носитель (одна
        // загрузка привязана к нескольким делам, ТНД-002) ЛИБО его копия по содержимому: тот же файл в деле с другим
        // грифом или подразделением хранится отдельным носителем (ключ дедупликации — подразделение, гриф, хеш;
        // ТБ-070). Копии — под floor'ом модуля «Медиа» (IMediaCatalog), дело — под floor'ом (otherCases), своё
        // дело исключено. sourceOf: носитель в чужом деле → свои носители с тем же содержимым (для счётчиков).
        var twins = await mediaCatalog.ListContentTwinsAsync(ownAssetIds, access, cancellationToken);
        var sourceOf = ownAssetIds.ToDictionary(id => id, id => new HashSet<int> { id });
        foreach (var twin in twins)
        {
            if (!sourceOf.TryGetValue(twin.TwinAssetId, out var sources))
            {
                sourceOf[twin.TwinAssetId] = sources = [];
            }

            sources.Add(twin.AssetId);
        }

        var materialAssetIds = sourceOf.Keys.ToList();
        var materialLinks = await db.CaseMediaLinks.AsNoTracking()
            .Where(l => materialAssetIds.Contains(l.MediaAssetId) && otherCases.Any(c => c.Id == l.CaseId))
            .Select(l => new { l.MediaAssetId, l.CaseId })
            .ToListAsync(cancellationToken);
        foreach (var group in materialLinks.GroupBy(l => l.CaseId))
        {
            var assets = group.SelectMany(l => sourceOf[l.MediaAssetId]).ToHashSet();
            var onCase = ownAppearances.Where(a => assets.Contains(a.MediaAssetId)).ToList();
            var last = onCase.Max(a => a.ConfirmedAtUtc);
            matches.Add(new Match(IntersectionKind.Face, FaceOnMaterialKey,
                string.Create(CultureInfo.InvariantCulture,
                    $"объект подтверждён на материалах этого дела: появлений {onCase.Count}, последнее {last:dd.MM.yyyy}"),
                group.Key));
        }

        // Вид 2: то же лицо (та же детекция на том же кадре) подтверждено у фигуранта ДРУГОГО дела — один человек
        // в двух делах. Чужое появление, чужой фигурант и его дело — каждый под floor'ом (otherPersons → otherCases).
        var sameFace = await db.Appearances.AsNoTracking()
            .Where(BaselineAccess.Filter<Appearance>(access))
            .Where(policy.BuildFilter<Appearance>(access))
            .Where(a => ownFaceIds.Contains(a.MediaFaceId) && a.PersonId != own.Id)
            .Join(otherPersons, a => a.PersonId, p => p.Id, (a, p) => new { a.MediaFaceId, p.CaseId })
            .ToListAsync(cancellationToken);
        foreach (var group in sameFace.GroupBy(f => f.CaseId))
        {
            var faces = group.Select(f => f.MediaFaceId).ToHashSet();
            var shared = ownAppearances.Count(a => faces.Contains(a.MediaFaceId));
            matches.Add(new Match(IntersectionKind.Face, FaceSharedKey,
                string.Create(CultureInfo.InvariantCulture,
                    $"то же лицо подтверждено у фигуранта этого дела: совпавших появлений объекта {shared}"),
                group.Key));
        }

        if (ownAppearances.Count > 0)
        {
            const string OwnFace = "лицо объекта (подтверждено двумя сотрудниками)";
            ownValues.TryAdd((IntersectionKind.Face, FaceOnMaterialKey), OwnFace);
            ownValues.TryAdd((IntersectionKind.Face, FaceSharedKey), OwnFace);
        }

        var caseIds = matches.Select(m => m.CaseId).Distinct().ToList();

        var cases = await db.Cases.AsNoTracking()
            .Where(BaselineAccess.Filter<CaseFile>(access))
            .Where(policy.BuildFilter<CaseFile>(access))
            .Where(c => caseIds.Contains(c.Id))
            .Select(c => new { c.Id, c.Number, c.Kind, c.Status, c.InvestigatorUserId })
            .ToDictionaryAsync(c => c.Id, cancellationToken);

        // Карточку чужого дела можно открыть только по правилу ТФ-ДЕЛ-03 (полная решётка с ролью).
        var openable = (await CaseAccessRule.Apply(db.Cases.AsNoTracking(), access, policy, role)
            .Where(c => caseIds.Contains(c.Id))
            .Select(c => c.Id)
            .ToListAsync(cancellationToken)).ToHashSet();

        var reviews = await db.IntersectionReviews.AsNoTracking()
            .Where(BaselineAccess.Filter<IntersectionReview>(access))
            .Where(policy.BuildFilter<IntersectionReview>(access))
            .Where(r => r.PersonId == own.Id)
            .ToListAsync(cancellationToken);
        var reviewByKey = reviews.ToDictionary(r => (r.Kind, r.KeyNormalized, r.OtherCaseId));

        var rows = matches
            .Where(m => cases.ContainsKey(m.CaseId))
            .GroupBy(m => (m.Kind, m.Key, m.CaseId))
            .Select(g =>
            {
                var c = cases[g.Key.CaseId];
                reviewByKey.TryGetValue((g.Key.Kind, g.Key.Key, g.Key.CaseId), out var review);
                return new IntersectionRow(
                    g.Key.Kind,
                    g.Key.Key,
                    ownValues.GetValueOrDefault((g.Key.Kind, g.Key.Key), g.Key.Key),
                    g.Select(m => m.Value).Order(StringComparer.Ordinal).First(),
                    c.Id,
                    c.Number,
                    c.Kind,
                    c.Status,
                    c.InvestigatorUserId,
                    openable.Contains(c.Id),
                    review?.Decision,
                    review?.DecidedByUserId,
                    review?.DecidedAt);
            })
            .OrderBy(r => r.Kind)
            .ThenBy(r => r.OtherCaseNumber, StringComparer.Ordinal)
            .ThenBy(r => r.Key, StringComparer.Ordinal)
            .ToList();

        return (own, rows);
    }

    // Ключи пересечения по лицу (решение по ним — на своей стороне, как у остальных видов, ADR-0029 п. 6).
    private const string FaceOnMaterialKey = "face:material";
    private const string FaceSharedKey = "face:shared";

    // Ключ пересечения по ФИО: нормализованное ФИО и дата рождения в инвариантном виде.
    private static string NameKey(string nameNormalized, DateOnly birthDate) =>
        $"{nameNormalized}|{birthDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";

    private static string NameValue(string displayName, DateOnly birthDate) =>
        $"{displayName}, {birthDate.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)}";

    // Роль для правила видимости дел: без права «Дашборд и реестр дел» (матрица доступа, ADR-0032) — null, и
    // CaseAccessRule вернёт пусто (ТБ-012/021).
    private Task<InvestigationRole?> ResolveRoleAsync(AccessContext access, CancellationToken cancellationToken) =>
        PermissionRule.ResolveCaseViewerAsync(roles, access.NumericSubjectId, cancellationToken);

    private sealed record OwnPerson(
        int Id, int CaseId, short Classification, int DivisionId, string DisplayName,
        string? NameNormalized, DateOnly? BirthDate, string? ResidenceNormalized, string? Residence);

    private sealed record Match(IntersectionKind Kind, string Key, string Value, int CaseId);
}
