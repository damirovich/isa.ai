using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Domain.Entities;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Profile.Investigation.Data;

/// <summary>
/// События — «дела-посредники» связи объектов поверх <c>investigation.link_event</c> и <c>link_event_participant</c>
/// (ТФ-ПЕР-11, ADR-0056). Нормализованные адрес и госномер контакта (ТО-мат-11) вычисляются здесь при каждой записи.
/// </summary>
/// <remarks>
/// ИНВАРИАНТ (ТБ-020/021, ТБ-084, ADR-0056 п. 3):
/// <list type="bullet">
/// <item>событие видно, если оно в пределах допуска (floor ядра и политика на строке события) и среди его участников
/// есть фигурант, доступный субъекту по полной решётке и роли (<see cref="PersonAccess"/>);</item>
/// <item>участник видимого события: доступный по роли — полностью; в пределах допуска (floor фигуранта и его дела без
/// сужения по роли, как «чужая» сторона пересечений) — только делом; выше допуска — никак, даже числом;</item>
/// <item>писать можно только через доступных по роли фигурантов и видимые события; недоступное неотличимо от
/// несуществующего (<see cref="LinkEventWriteResult.NotFound"/>).</item>
/// </list>
/// Подзапросы решётки в запросах с отслеживанием строятся с <c>tracking: true</c>: <c>AsNoTracking</c> в любом месте
/// дерева выражения EF Core применяет ко всему запросу (см. <see cref="PersonAccess.Accessible"/>).
/// </remarks>
public sealed class LinkEventStore(
    IDbContextFactory<InvestigationDbContext> contextFactory,
    IAccessPolicy policy,
    IUserRoleStore roles) : ILinkEventStore
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<LinkEventRow>?> ListForPersonAsync(
        int personId, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);

        var role = await ResolveRoleAsync(access, cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        if (!await PersonAccess.Accessible(db, access, policy, role).AnyAsync(p => p.Id == personId, cancellationToken))
        {
            return null;
        }

        var participants = Participants(db, access);
        var eventIds = await VisibleEvents(db, access, role, tracking: false)
            .Where(e => participants.Any(p => p.EventId == e.Id && p.PersonId == personId))
            .Select(e => e.Id)
            .ToListAsync(cancellationToken);
        return await LoadAsync(db, eventIds, access, role, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<LinkEventRow?> GetAsync(int eventId, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);

        var role = await ResolveRoleAsync(access, cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var eventIds = await VisibleEvents(db, access, role, tracking: false)
            .Where(e => e.Id == eventId)
            .Select(e => e.Id)
            .ToListAsync(cancellationToken);
        return (await LoadAsync(db, eventIds, access, role, cancellationToken)).SingleOrDefault();
    }

    /// <inheritdoc />
    public async Task<(LinkEventWriteResult Result, int EventId)> LinkAsync(
        LinkPersonsDraft draft, AccessContext access, int? userId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(access);
        if ((draft.EventId is null) == (draft.NewEvent is null))
        {
            throw new ArgumentException("Связь заводится через существующее событие или через новое — ровно одно из двух.", nameof(draft));
        }

        // [АИС табл. 7.1 № 3]: ребро «объект — сам на себя» запрещено — до любого чтения.
        if (draft.PersonId == draft.OtherPersonId)
        {
            return (LinkEventWriteResult.SelfLink, 0);
        }

        var role = await ResolveRoleAsync(access, cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Оба объекта — по полной решётке и роли: объект вне допуска для связи не выбирается (ТФ-ПЕР-11).
        var objects = await PersonAccess.Accessible(db, access, policy, role)
            .Where(p => p.Id == draft.PersonId || p.Id == draft.OtherPersonId)
            .Select(p => new { p.Id, p.Classification, p.DivisionId })
            .ToListAsync(cancellationToken);
        if (objects.Count != 2)
        {
            return (LinkEventWriteResult.NotFound, 0);
        }

        var first = objects.Single(p => p.Id == draft.PersonId);
        var second = objects.Single(p => p.Id == draft.OtherPersonId);

        LinkEvent linkEvent;
        var present = new HashSet<int>();
        if (draft.EventId is { } eventId)
        {
            var found = await VisibleEvents(db, access, role, tracking: true).FirstOrDefaultAsync(e => e.Id == eventId, cancellationToken);
            if (found is null)
            {
                return (LinkEventWriteResult.NotFound, 0);
            }

            linkEvent = found;
            present.UnionWith(await db.LinkEventParticipants
                .Where(p => p.EventId == eventId && (p.PersonId == first.Id || p.PersonId == second.Id))
                .Select(p => p.PersonId)
                .ToListAsync(cancellationToken));
            if (present.Contains(first.Id) && present.Contains(second.Id))
            {
                return (LinkEventWriteResult.AlreadyLinked, eventId);
            }
        }
        else
        {
            // Подразделение — дела объекта, из карточки которого заводится событие (ADR-0056 п. 2): у субъекта их может
            // быть несколько, а работает он в этом деле.
            linkEvent = new LinkEvent
            {
                Title = string.Empty,
                CreatedByUserId = userId,
                DivisionId = first.DivisionId,
            };
            Apply(linkEvent, draft.NewEvent!);
            db.LinkEvents.Add(linkEvent);
        }

        foreach (var (person, personRole) in new[] { (first, draft.PersonRole), (second, draft.OtherPersonRole) })
        {
            if (present.Contains(person.Id))
            {
                continue; // уже участвует — его запись не меняется
            }

            // Гриф и подразделение участия — фигуранта (ТБ-070): адрес и госномер контакта — сведения об объекте.
            var participant = new LinkEventParticipant
            {
                PersonId = person.Id,
                Classification = person.Classification,
                DivisionId = person.DivisionId,
            };
            Apply(participant, draft.Contact with { Role = personRole });
            linkEvent.Participants.Add(participant);
        }

        // ИНВАРИАНТ (вопрос 26): гриф события не ниже грифа любого участника — поднимается, никогда не опускается.
        linkEvent.Classification = Math.Max(linkEvent.Classification, Math.Max(first.Classification, second.Classification));

        await db.SaveChangesAsync(cancellationToken);
        return (LinkEventWriteResult.Ok, linkEvent.Id);
    }

    /// <inheritdoc />
    public async Task<LinkEventWriteResult> UpdateEventAsync(
        int eventId, LinkEventDraft draft, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(access);

        var role = await ResolveRoleAsync(access, cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var linkEvent = await VisibleEvents(db, access, role, tracking: true).FirstOrDefaultAsync(e => e.Id == eventId, cancellationToken);
        if (linkEvent is null)
        {
            return LinkEventWriteResult.NotFound;
        }

        Apply(linkEvent, draft);
        await db.SaveChangesAsync(cancellationToken);
        return LinkEventWriteResult.Ok;
    }

    /// <inheritdoc />
    public async Task<LinkEventWriteResult> UpdateParticipantAsync(
        int participantId, LinkParticipantDraft draft, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(access);

        var role = await ResolveRoleAsync(access, cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var participant = await EditableParticipant(db, participantId, access, role).FirstOrDefaultAsync(cancellationToken);
        if (participant is null)
        {
            return LinkEventWriteResult.NotFound;
        }

        Apply(participant, draft);
        await db.SaveChangesAsync(cancellationToken);
        return LinkEventWriteResult.Ok;
    }

    /// <inheritdoc />
    public async Task<LinkEventWriteResult> RemoveParticipantAsync(
        int participantId, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);

        var role = await ResolveRoleAsync(access, cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var participant = await EditableParticipant(db, participantId, access, role).FirstOrDefaultAsync(cancellationToken);
        if (participant is null)
        {
            return LinkEventWriteResult.NotFound;
        }

        // Одна транзакция: участие ушло, и событие без участников — вместе с ним (ADR-0056 п. 2).
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var eventId = participant.EventId;
        db.LinkEventParticipants.Remove(participant);
        await db.SaveChangesAsync(cancellationToken);
        await db.LinkEvents
            .Where(e => e.Id == eventId && !db.LinkEventParticipants.Any(p => p.EventId == e.Id))
            .ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return LinkEventWriteResult.Ok;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Текст — название и описание события, роль участника, марка транспорта (без учёта регистра, «ё» = «е»); адрес и
    /// госномер контакта — по НОРМАЛИЗОВАННЫМ значениям, как в поиске адресов и транспорта (ТО-мат-11). Только участники
    /// с отметкой «в общем поиске» и уровнем не «неверно», доступные субъекту по решётке и роли, в делах области поиска;
    /// событие — под своим floor'ом (ADR-0056 п. 5).
    /// </remarks>
    public async Task<IReadOnlyList<EventSearchRow>> SearchAsync(
        SearchTerms terms, IReadOnlyCollection<int> caseIds, int limit, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(terms);
        ArgumentNullException.ThrowIfNull(caseIds);
        ArgumentNullException.ThrowIfNull(access);
        if (caseIds.Count == 0 || limit <= 0)
        {
            return [];
        }

        var role = await ResolveRoleAsync(access, cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var ids = caseIds.ToArray();
        var persons = PersonAccess.Accessible(db, access, policy, role).Where(p => ids.Contains(p.CaseId));
        var events = db.LinkEvents
            .Where(BaselineAccess.Filter<LinkEvent>(access))
            .Where(policy.BuildFilter<LinkEvent>(access));
        var text = "%" + PersonStore.EscapeLike(terms.Text) + "%";
        var address = terms.Address is { } normalizedAddress ? "%" + PersonStore.EscapeLike(normalizedAddress) + "%" : null;
        var plate = terms.Plate is { } normalizedPlate ? "%" + PersonStore.EscapeLike(normalizedPlate) + "%" : null;

        return await Participants(db, access)
            .AsNoTracking()
            .Where(p => p.InSearch && p.Level != LinkLevel.Wrong)
            .Join(events, p => p.EventId, e => e.Id, (p, e) => new { Participant = p, Event = e })
            .Join(persons, x => x.Participant.PersonId, person => person.Id,
                (x, person) => new { x.Participant, x.Event, person.DisplayName, person.CaseId })
            .Where(x => EF.Functions.ILike(x.Event.Title.Replace("ё", "е").Replace("Ё", "Е"), text, "\\")
                || (x.Event.Description != null && EF.Functions.ILike(x.Event.Description.Replace("ё", "е").Replace("Ё", "Е"), text, "\\"))
                || (x.Participant.Role != null && EF.Functions.ILike(x.Participant.Role.Replace("ё", "е").Replace("Ё", "Е"), text, "\\"))
                || (x.Participant.VehicleDetails != null && EF.Functions.ILike(x.Participant.VehicleDetails, text, "\\"))
                || (address != null && x.Participant.ContactAddressNormalized != null
                    && EF.Functions.Like(x.Participant.ContactAddressNormalized, address, "\\"))
                || (plate != null && x.Participant.VehiclePlateNormalized != null
                    && EF.Functions.Like(x.Participant.VehiclePlateNormalized, plate, "\\")))
            .OrderByDescending(x => x.Event.OccurredOn.HasValue)
            .ThenByDescending(x => x.Event.OccurredOn)
            .ThenByDescending(x => x.Event.Id)
            .ThenBy(x => x.Participant.Id)
            .Take(limit)
            .Select(x => new EventSearchRow(
                x.Event.Id, x.Event.Kind, x.Event.Title, x.Event.OccurredOn, x.Participant.PersonId, x.DisplayName, x.CaseId,
                x.Participant.Role, x.Participant.ContactAddress, x.Participant.VehiclePlate))
            .ToListAsync(cancellationToken);
    }

    // Строки участия под собственным floor'ом и политикой (у них свои режимные поля, ТБ-070). Без AsNoTracking —
    // годится и для подзапросов в запросах с отслеживанием.
    private IQueryable<LinkEventParticipant> Participants(InvestigationDbContext db, AccessContext access) =>
        db.LinkEventParticipants
            .Where(BaselineAccess.Filter<LinkEventParticipant>(access))
            .Where(policy.BuildFilter<LinkEventParticipant>(access));

    // Видимые события (ADR-0056 п. 3): floor и политика на событии плюс хотя бы один участник, доступный по роли.
    private IQueryable<LinkEvent> VisibleEvents(InvestigationDbContext db, AccessContext access, InvestigationRole? role, bool tracking)
    {
        var accessible = PersonAccess.Accessible(db, access, policy, role, tracking);
        var participants = Participants(db, access);
        var events = tracking ? db.LinkEvents : db.LinkEvents.AsNoTracking();
        return events
            .Where(BaselineAccess.Filter<LinkEvent>(access))
            .Where(policy.BuildFilter<LinkEvent>(access))
            .Where(e => participants.Any(p => p.EventId == e.Id && accessible.Any(person => person.Id == p.PersonId)));
    }

    // Участие, которое субъект может менять: его фигурант доступен по роли, а событие видно.
    private IQueryable<LinkEventParticipant> EditableParticipant(
        InvestigationDbContext db, int participantId, AccessContext access, InvestigationRole? role)
    {
        var accessible = PersonAccess.Accessible(db, access, policy, role, tracking: true);
        var events = VisibleEvents(db, access, role, tracking: true);
        return Participants(db, access)
            .Where(p => p.Id == participantId
                && accessible.Any(person => person.Id == p.PersonId)
                && events.Any(e => e.Id == p.EventId));
    }

    // События с видимыми участниками. Запросы выполняются всегда, и при пустом списке: их состав не зависит от данных
    // (ТБ-021). «Чужая» сторона — floor фигуранта и его дела без сужения по роли (как в пересечениях, ТБ-084).
    private async Task<IReadOnlyList<LinkEventRow>> LoadAsync(
        InvestigationDbContext db, List<int> eventIds, AccessContext access, InvestigationRole? role, CancellationToken cancellationToken)
    {
        var events = await db.LinkEvents.AsNoTracking()
            .Where(e => eventIds.Contains(e.Id))
            .ToListAsync(cancellationToken);

        var floorCases = db.Cases
            .Where(BaselineAccess.Filter<CaseFile>(access))
            .Where(policy.BuildFilter<CaseFile>(access));
        var floorPersons = db.Persons
            .Where(BaselineAccess.Filter<Person>(access))
            .Where(policy.BuildFilter<Person>(access));
        var rows = await Participants(db, access)
            .AsNoTracking()
            .Where(p => eventIds.Contains(p.EventId))
            .Join(floorPersons, p => p.PersonId, person => person.Id,
                (p, person) => new { Participant = p, person.DisplayName, person.Alias, person.CaseId })
            .Join(floorCases, x => x.CaseId, c => c.Id,
                (x, c) => new { x.Participant, x.DisplayName, x.Alias, x.CaseId, c.Number, c.Kind, c.Status, c.InvestigatorUserId })
            .ToListAsync(cancellationToken);

        var personIds = rows.Select(r => r.Participant.PersonId).Distinct().ToArray();
        var accessible = (await PersonAccess.Accessible(db, access, policy, role)
            .Where(p => personIds.Contains(p.Id))
            .Select(p => p.Id)
            .ToListAsync(cancellationToken)).ToHashSet();

        var byEvent = rows
            .Select(r =>
            {
                var open = accessible.Contains(r.Participant.PersonId);
                var p = r.Participant;
                return (p.EventId, Row: new LinkEventParticipantRow(
                    p.Id,
                    open ? p.PersonId : null,
                    open ? r.DisplayName : null,
                    open ? r.Alias : null,
                    r.CaseId,
                    r.Number,
                    r.Kind,
                    r.Status,
                    r.InvestigatorUserId,
                    open,
                    open ? p.Role : null,
                    p.Level,
                    open && p.InSearch,
                    open ? p.VehiclePlate : null,
                    open ? p.VehicleDetails : null,
                    open ? p.ContactAddress : null,
                    p.Classification));
            })
            .GroupBy(x => x.EventId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<LinkEventParticipantRow>)g.Select(x => x.Row)
                    .OrderByDescending(row => row.IsAccessible)
                    .ThenBy(row => row.CaseNumber, StringComparer.Ordinal)
                    .ThenBy(row => row.Id)
                    .ToList());

        return events
            .OrderByDescending(e => e.OccurredOn.HasValue)
            .ThenByDescending(e => e.OccurredOn)
            .ThenByDescending(e => e.Id)
            .Select(e => new LinkEventRow(
                e.Id, e.Kind, e.Title, e.OccurredOn, e.Description, e.Classification, e.DivisionId, e.CreatedByUserId, e.CreatedAt,
                byEvent.GetValueOrDefault(e.Id) ?? []))
            .ToList();
    }

    private static void Apply(LinkEvent linkEvent, LinkEventDraft draft)
    {
        linkEvent.Kind = draft.Kind;
        linkEvent.Title = draft.Title.Trim();
        linkEvent.OccurredOn = draft.OccurredOn;
        linkEvent.Description = Clean(draft.Description);
    }

    private static void Apply(LinkEventParticipant participant, LinkParticipantDraft draft)
    {
        participant.Role = Clean(draft.Role);
        participant.Level = draft.Level;
        participant.InSearch = draft.InSearch;
        participant.VehiclePlate = Clean(draft.VehiclePlate);
        participant.VehiclePlateNormalized = RequisiteNormalizer.Plate(participant.VehiclePlate);
        participant.VehicleDetails = Clean(draft.VehicleDetails);
        participant.ContactAddress = Clean(draft.ContactAddress);
        participant.ContactAddressNormalized = RequisiteNormalizer.Address(participant.ContactAddress);
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private Task<InvestigationRole?> ResolveRoleAsync(AccessContext access, CancellationToken cancellationToken) =>
        PermissionRule.ResolveCaseViewerAsync(roles, access.NumericSubjectId, cancellationToken);
}
