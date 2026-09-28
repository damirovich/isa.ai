using System.Globalization;
using ISC.AI.Abstractions.Security;
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
    IUserRoleStore roles) : IIntersectionStore
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

    // Ключ пересечения по лицу: нормализованное ФИО и дата рождения в инвариантном виде.
    private static string NameKey(string nameNormalized, DateOnly birthDate) =>
        $"{nameNormalized}|{birthDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";

    private static string NameValue(string displayName, DateOnly birthDate) =>
        $"{displayName}, {birthDate.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)}";

    private async Task<InvestigationRole?> ResolveRoleAsync(AccessContext access, CancellationToken cancellationToken) =>
        access.NumericSubjectId is { } userId
            ? await roles.GetRoleAsync(userId, cancellationToken)
            : null;

    private sealed record OwnPerson(
        int Id, int CaseId, short Classification, int DivisionId, string DisplayName,
        string? NameNormalized, DateOnly? BirthDate, string? ResidenceNormalized, string? Residence);

    private sealed record Match(IntersectionKind Kind, string Key, string Value, int CaseId);
}
