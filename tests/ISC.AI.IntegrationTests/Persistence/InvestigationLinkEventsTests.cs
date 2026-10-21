using System;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Data;
using ISC.AI.Profile.Investigation.Application.Features.RelationGraph;
using ISC.AI.Profile.Investigation.Data;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Shouldly;
using Testcontainers.PostgreSql;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Связь объектов через событие — «дело-посредник» (ТФ-ПЕР-11, ADR-0056) на настоящем Postgres: событие видно только
/// в пределах допуска и при доступном участнике; участник вне дел субъекта по роли — только делом, из подразделения вне
/// допуска и выше допуска — никак (ТБ-084, GATE-7); гриф события не ниже грифов участников; самосвязь, повтор и
/// недоступное — отказ; правка и исключение участия; адрес и госномер контакта дают пересечения по местам посещения
/// только с отметкой «в общем поиске» и не внутри одного события; единый поиск; граф; уничтожение дела; ограничения.
/// </summary>
[Trait("Category", "Gate")]
public sealed class InvestigationLinkEventsTests : IAsyncLifetime
{
    private const int Owner = 10;
    private const int Colleague = 11;
    private const int Outsider = 12;
    private const int Head = 20;

    private static readonly LinkParticipantDraft Contact =
        new(null, LinkLevel.Confirmed, true, "01 kg 123 abc", "Toyota Camry", "г. Бишкек, ул. Токтогула, д. 1");

    private readonly PostgreSqlContainer _postgres = TestPostgres.Create();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "Видимость: событие — при доступном участнике; чужой участник — делом без ФИО; другое подразделение и выше допуска — никак; гриф события поднимается")]
    public async Task Events_follow_the_lattice()
    {
        var kit = await ArrangeAsync();
        var s = await SeedAsync(kit);
        var owner = InvestigationTestKit.Access(Owner, 3, 5);
        var head = InvestigationTestKit.Access(Head, 4, 5, 6);

        var (linked, eventId) = await kit.Events.LinkAsync(Link(s.P1, s.P2, NewEvent("Встреча у рынка")), owner, Owner);
        linked.ShouldBe(LinkEventWriteResult.Ok);
        (await kit.Events.LinkAsync(Link(s.P1, s.PB, null) with { EventId = eventId }, head, Head)).Result.ShouldBe(LinkEventWriteResult.Ok);
        (await kit.Events.LinkAsync(Link(s.P1, s.PD, null) with { EventId = eventId }, head, Head)).Result.ShouldBe(LinkEventWriteResult.Ok);

        await using (var db = kit.Factory.CreateDbContext())
        {
            var stored = await db.LinkEvents.AsNoTracking().SingleAsync(e => e.Id == eventId);
            stored.Classification.ShouldBe<short>(3, "не ниже грифа дела А-2");
            stored.DivisionId.ShouldBe(5, "подразделение дела объекта, из карточки которого заведено");
            stored.CreatedByUserId.ShouldBe(Owner);
            (await db.LinkEventParticipants.CountAsync(p => p.EventId == eventId)).ShouldBe(4);
        }

        // Следователь: свои два объекта — полностью; объект коллеги — только делом (ТБ-084); подразделение 6 — никак.
        var mine = (await kit.Events.ListForPersonAsync(s.P1, owner)).ShouldNotBeNull().ShouldHaveSingleItem();
        mine.Title.ShouldBe("Встреча у рынка");
        mine.Participants.Count.ShouldBe(3);
        mine.Participants.Where(p => p.IsAccessible).Select(p => p.PersonId).ShouldBe([s.P1, s.P2], ignoreOrder: true);
        var foreign = mine.Participants.Single(p => !p.IsAccessible);
        foreign.CaseNumber.ShouldBe("Б-1");
        foreign.PersonId.ShouldBeNull();
        foreign.PersonName.ShouldBeNull();
        foreign.ContactAddress.ShouldBeNull();
        foreign.ResponsibleUserId.ShouldBe(Colleague);

        // Коллега видит событие через своего объекта; объекты следователя — только делами.
        var colleague = InvestigationTestKit.Access(Colleague, 3, 5);
        var theirs = (await kit.Events.ListForPersonAsync(s.PB, colleague)).ShouldNotBeNull().ShouldHaveSingleItem();
        theirs.Participants.Single(p => p.IsAccessible).PersonId.ShouldBe(s.PB);
        theirs.Participants.Where(p => !p.IsAccessible).Select(p => p.CaseNumber).ShouldBe(["А-1", "А-2"], ignoreOrder: true);
        (await kit.Events.ListForPersonAsync(s.P1, colleague)).ShouldBeNull();

        // Без доступного участника события нет вовсе.
        (await kit.Events.GetAsync(eventId, InvestigationTestKit.Access(Outsider, 4, 5, 6))).ShouldBeNull();

        // Объект с грифом 4: гриф события поднимается до 4 — следователю с допуском 3 событие больше не видно ничем.
        var (secretLink, secretEvent) = await kit.Events.LinkAsync(Link(s.P1, s.PS, NewEvent("Передача")), head, Head);
        secretLink.ShouldBe(LinkEventWriteResult.Ok);
        (await kit.Events.ListForPersonAsync(s.P1, owner)).ShouldNotBeNull().Select(e => e.Id).ShouldBe([eventId]);
        (await kit.Events.GetAsync(secretEvent, owner)).ShouldBeNull();
        (await kit.Events.GetAsync(secretEvent, head)).ShouldNotBeNull().Classification.ShouldBe<short>(4);
    }

    [Fact(DisplayName = "Отказы: самосвязь, повтор, объект вне дел по роли, невидимое событие — без записи")]
    public async Task Invalid_links_are_rejected()
    {
        var kit = await ArrangeAsync();
        var s = await SeedAsync(kit);
        var owner = InvestigationTestKit.Access(Owner, 3, 5);
        var colleague = InvestigationTestKit.Access(Colleague, 3, 5);

        (await kit.Events.LinkAsync(Link(s.P1, s.P1, NewEvent("Сам с собой")), owner, Owner)).Result.ShouldBe(LinkEventWriteResult.SelfLink);
        (await kit.Events.LinkAsync(Link(s.P1, s.PB, NewEvent("Чужой объект")), owner, Owner)).Result.ShouldBe(LinkEventWriteResult.NotFound);

        var (_, eventId) = await kit.Events.LinkAsync(Link(s.P1, s.P2, NewEvent("Встреча")), owner, Owner);
        (await kit.Events.LinkAsync(Link(s.P2, s.P1, null) with { EventId = eventId }, owner, Owner))
            .Result.ShouldBe(LinkEventWriteResult.AlreadyLinked);

        // Событие коллеги между его объектами следователю не видно — через него не связать.
        var (_, theirs) = await kit.Events.LinkAsync(Link(s.PB, s.PB2, NewEvent("Их встреча")), colleague, Colleague);
        (await kit.Events.LinkAsync(Link(s.P1, s.P2, null) with { EventId = theirs }, owner, Owner))
            .Result.ShouldBe(LinkEventWriteResult.NotFound);

        await using var db = kit.Factory.CreateDbContext();
        (await db.LinkEvents.CountAsync()).ShouldBe(2);
        (await db.LinkEventParticipants.CountAsync()).ShouldBe(4);
    }

    [Fact(DisplayName = "Правка и исключение: нормализация госномера и адреса, гриф участия — фигуранта; чужому — «не найден»; без участников событие удаляется")]
    public async Task Participation_is_edited_and_removed()
    {
        var kit = await ArrangeAsync();
        var s = await SeedAsync(kit);
        var owner = InvestigationTestKit.Access(Owner, 3, 5);
        var colleague = InvestigationTestKit.Access(Colleague, 3, 5);

        var (_, eventId) = await kit.Events.LinkAsync(Link(s.P1, s.P2, NewEvent("Встреча"), "водитель", "пассажир"), owner, Owner);
        var row = (await kit.Events.GetAsync(eventId, owner)).ShouldNotBeNull();
        var first = row.Participants.Single(p => p.PersonId == s.P1);
        var second = row.Participants.Single(p => p.PersonId == s.P2);
        first.Role.ShouldBe("водитель");
        second.Role.ShouldBe("пассажир");
        first.VehiclePlate.ShouldBe("01 kg 123 abc");

        await using (var db = kit.Factory.CreateDbContext())
        {
            var stored = await db.LinkEventParticipants.AsNoTracking().SingleAsync(p => p.Id == second.Id);
            stored.VehiclePlateNormalized.ShouldBe("01KG123ABC");
            stored.ContactAddressNormalized.ShouldBe("бишкек ул токтогула 1");
            stored.Classification.ShouldBe<short>(3, "гриф участия — фигуранта (дело А-2)");
            stored.DivisionId.ShouldBe(5);
        }

        (await kit.Events.UpdateParticipantAsync(first.Id, Contact with { Level = LinkLevel.Wrong, InSearch = false, VehiclePlate = null }, owner))
            .ShouldBe(LinkEventWriteResult.Ok);
        (await kit.Events.UpdateParticipantAsync(first.Id, Contact, colleague)).ShouldBe(LinkEventWriteResult.NotFound);
        (await kit.Events.UpdateEventAsync(eventId, NewEvent("Встреча у рынка «Ош»")!, owner)).ShouldBe(LinkEventWriteResult.Ok);
        (await kit.Events.UpdateEventAsync(eventId, NewEvent("Чужая правка")!, colleague)).ShouldBe(LinkEventWriteResult.NotFound);
        (await kit.Events.RemoveParticipantAsync(second.Id, colleague)).ShouldBe(LinkEventWriteResult.NotFound);

        var edited = (await kit.Events.GetAsync(eventId, owner)).ShouldNotBeNull();
        edited.Title.ShouldBe("Встреча у рынка «Ош»");
        var wrong = edited.Participants.Single(p => p.PersonId == s.P1);
        wrong.Level.ShouldBe(LinkLevel.Wrong);
        wrong.InSearch.ShouldBeFalse();
        wrong.VehiclePlate.ShouldBeNull();

        // Исключение: одно — событие остаётся с другим участником; последнее — событие удаляется.
        (await kit.Events.RemoveParticipantAsync(second.Id, owner)).ShouldBe(LinkEventWriteResult.Ok);
        (await kit.Events.GetAsync(eventId, owner)).ShouldNotBeNull().Participants.ShouldHaveSingleItem().PersonId.ShouldBe(s.P1);
        (await kit.Events.RemoveParticipantAsync(first.Id, owner)).ShouldBe(LinkEventWriteResult.Ok);
        await using (var db = kit.Factory.CreateDbContext())
        {
            (await db.LinkEvents.CountAsync()).ShouldBe(0);
        }
    }

    [Fact(DisplayName = "Места посещения: адрес и госномер контакта дают пересечения с другим делом только с отметкой «в общем поиске»; внутри одного события — нет")]
    public async Task Contact_places_intersect_only_when_in_search()
    {
        var kit = await ArrangeAsync();
        var s = await SeedAsync(kit);
        var owner = InvestigationTestKit.Access(Owner, 3, 5);
        var colleague = InvestigationTestKit.Access(Colleague, 3, 5);

        // У объекта коллеги — тот же адрес строкой реквизита и тот же госномер, записанные иначе.
        (await kit.Requisites.SaveAddressAsync(s.PB, null, new PersonAddressDraft(AddressKind.Stay, "Бишкек, улица Токтогула 1", null), colleague))
            .Result.ShouldBe(PersonWriteResult.Ok);
        (await kit.Requisites.SaveVehicleAsync(s.PB, null, new PersonVehicleDraft("01-KG-123-ABC", null, null, null, null), colleague))
            .Result.ShouldBe(PersonWriteResult.Ok);

        var (_, eventId) = await kit.Events.LinkAsync(Link(s.P1, s.P2, NewEvent("Встреча")), owner, Owner);

        var rows = (await kit.Intersections.FindForPersonAsync(s.P1, owner)).ShouldNotBeNull();
        var address = rows.Single(r => r.Kind == IntersectionKind.Address);
        address.OtherCaseId.ShouldBe(s.CaseB);
        address.OwnValue.ShouldBe("г. Бишкек, ул. Токтогула, д. 1");
        rows.Single(r => r.Kind == IntersectionKind.Vehicle).OtherCaseId.ShouldBe(s.CaseB);

        // Второй участник того же события с тем же адресом контакта — из дела А-2, но это не пересечение: связь уже заведена.
        rows.ShouldNotContain(r => r.OtherCaseId == s.CaseA2);

        // Снятая отметка — адрес и госномер контакта в пересечения не идут.
        var first = (await kit.Events.GetAsync(eventId, owner)).ShouldNotBeNull().Participants.Single(p => p.PersonId == s.P1);
        (await kit.Events.UpdateParticipantAsync(first.Id, Contact with { InSearch = false }, owner)).ShouldBe(LinkEventWriteResult.Ok);
        (await kit.Intersections.FindForPersonAsync(s.P1, owner)).ShouldNotBeNull().ShouldBeEmpty();

        // Другая сторона: адрес контакта участников события коллеги совпадает с адресом объекта — пересечение есть, пока
        // их участие отмечено «в общем поиске».
        (await kit.Requisites.SaveAddressAsync(s.P2, null, new PersonAddressDraft(AddressKind.Residence, "Каракол, улица Мира 5", null), owner))
            .Result.ShouldBe(PersonWriteResult.Ok);
        var (_, theirs) = await kit.Events.LinkAsync(
            Link(s.PB, s.PB2, NewEvent("Их встреча"), contact: Contact with { ContactAddress = "г. Каракол, ул. Мира, 5", VehiclePlate = null }),
            colleague, Colleague);
        var withOther = (await kit.Intersections.FindForPersonAsync(s.P2, owner)).ShouldNotBeNull();
        withOther.ShouldContain(r => r.Kind == IntersectionKind.Address && r.OtherCaseId == s.CaseB && r.OtherValue == "г. Каракол, ул. Мира, 5");

        foreach (var participant in (await kit.Events.GetAsync(theirs, colleague)).ShouldNotBeNull().Participants)
        {
            (await kit.Events.UpdateParticipantAsync(participant.Id, Contact with { ContactAddress = "г. Каракол, ул. Мира, 5", InSearch = false }, colleague))
                .ShouldBe(LinkEventWriteResult.Ok);
        }

        (await kit.Intersections.FindForPersonAsync(s.P2, owner)).ShouldNotBeNull()
            .ShouldNotContain(r => r.OtherValue == "г. Каракол, ул. Мира, 5");
    }

    [Fact(DisplayName = "Единый поиск: событие по названию, адресу и госномеру контакта — по участникам с отметкой и не «неверно», в делах области")]
    public async Task Events_are_found_by_unified_search()
    {
        var kit = await ArrangeAsync();
        var s = await SeedAsync(kit);
        var owner = InvestigationTestKit.Access(Owner, 3, 5);
        int[] all = [s.CaseA1, s.CaseA2, s.CaseB];

        var (_, eventId) = await kit.Events.LinkAsync(Link(s.P1, s.P2, NewEvent("Встреча у рынка"), "водитель"), owner, Owner);

        (await kit.Events.SearchAsync(SearchTerms.Parse("РЫНКА"), all, 10, owner)).Select(r => r.PersonId).ShouldBe([s.P1, s.P2], ignoreOrder: true);
        (await kit.Events.SearchAsync(SearchTerms.Parse("улица Токтогула"), all, 10, owner)).Count.ShouldBe(2);
        (await kit.Events.SearchAsync(SearchTerms.Parse("123 авс"), all, 10, owner)).Count.ShouldBe(2);
        (await kit.Events.SearchAsync(SearchTerms.Parse("водитель"), all, 10, owner)).ShouldHaveSingleItem().PersonId.ShouldBe(s.P1);
        (await kit.Events.SearchAsync(SearchTerms.Parse("рынка"), [s.CaseA2], 10, owner)).ShouldHaveSingleItem().PersonId.ShouldBe(s.P2);
        (await kit.Events.SearchAsync(SearchTerms.Parse("рынка"), all, 10, InvestigationTestKit.Access(Colleague, 3, 5))).ShouldBeEmpty();
        (await kit.Events.SearchAsync(SearchTerms.Parse("_%"), all, 10, owner)).ShouldBeEmpty();

        var row = (await kit.Events.GetAsync(eventId, owner)).ShouldNotBeNull();
        var first = row.Participants.Single(p => p.PersonId == s.P1);
        var second = row.Participants.Single(p => p.PersonId == s.P2);
        (await kit.Events.UpdateParticipantAsync(first.Id, Contact with { InSearch = false }, owner)).ShouldBe(LinkEventWriteResult.Ok);
        (await kit.Events.UpdateParticipantAsync(second.Id, Contact with { Level = LinkLevel.Wrong }, owner)).ShouldBe(LinkEventWriteResult.Ok);
        (await kit.Events.SearchAsync(SearchTerms.Parse("рынка"), all, 10, owner)).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Граф под решёткой: событие — узел, участник чужого дела — «чужое дело», подразделение вне допуска — никак; цепочка через событие")]
    public async Task Graph_shows_events_under_lattice()
    {
        var kit = await ArrangeAsync();
        var s = await SeedAsync(kit);
        var owner = InvestigationTestKit.Access(Owner, 3, 5);
        var head = InvestigationTestKit.Access(Head, 4, 5, 6);

        var (_, eventId) = await kit.Events.LinkAsync(Link(s.P1, s.P2, NewEvent("Встреча")), owner, Owner);
        await kit.Events.LinkAsync(Link(s.P1, s.PB, null) with { EventId = eventId }, head, Head);
        await kit.Events.LinkAsync(Link(s.P1, s.PD, null) with { EventId = eventId }, head, Head);

        var graph = (await Graph(kit, owner).Handle(new GetRelationGraphQuery(RelationNodeKind.Event, eventId, Depth: 1), CancellationToken.None))
            .Data.ShouldNotBeNull();
        graph.Nodes.Where(n => n.Kind == RelationNodeKind.Person).Select(n => n.EntityId).ShouldBe([s.P1, s.P2], ignoreOrder: true);
        graph.Nodes.Where(n => n.Kind == RelationNodeKind.ForeignCase).ShouldHaveSingleItem().Title.ShouldBe("Дело № Б-1");
        graph.Nodes.ShouldNotContain(n => n.Title.Contains("Д-6", StringComparison.Ordinal) || n.Title == "Объект Дэ");

        // Цепочка: объект А-1 → событие → объект А-2 (у руководителя — и до объекта коллеги).
        var path = (await Path(kit, head).Handle(new FindRelationPathQuery(RelationNodeKind.Person, s.P2, s.PB), CancellationToken.None))
            .Data.ShouldNotBeNull();
        path.Nodes.Select(n => n.Key).ShouldBe(
            [RelationNodeKeys.Person(s.P2), RelationNodeKeys.Event(eventId), RelationNodeKeys.Person(s.PB)]);
    }

    [Fact(DisplayName = "Уничтожение дела: участие уходит с фигурантами; событие без участников удаляется, с участниками других дел — остаётся")]
    public async Task Purge_removes_empty_events_only()
    {
        var kit = await ArrangeAsync();
        var s = await SeedAsync(kit);
        var owner = InvestigationTestKit.Access(Owner, 3, 5);
        var colleague = InvestigationTestKit.Access(Colleague, 9, 5, 6);

        var (_, shared) = await kit.Events.LinkAsync(Link(s.P1, s.P2, NewEvent("Общее")), owner, Owner);
        await kit.Events.LinkAsync(Link(s.P1, s.PB, null) with { EventId = shared }, InvestigationTestKit.Access(Head, 4, 5, 6), Head);
        var (_, own) = await kit.Events.LinkAsync(Link(s.P1, s.P2, NewEvent("Только своё")), owner, Owner);

        (await kit.Cases.PurgeAsync(s.CaseA1, owner)).ShouldBe(CaseWriteResult.Ok);
        (await kit.Cases.PurgeAsync(s.CaseA2, owner)).ShouldBe(CaseWriteResult.Ok);

        await using (var db = kit.Factory.CreateDbContext())
        {
            (await db.LinkEvents.AnyAsync(e => e.Id == own)).ShouldBeFalse();
            (await db.LinkEvents.AnyAsync(e => e.Id == shared)).ShouldBeTrue();
            (await db.LinkEventParticipants.Where(p => p.EventId == shared).Select(p => p.PersonId).ToListAsync()).ShouldBe([s.PB]);
        }

        (await kit.Cases.PurgeAsync(s.CaseB, colleague)).ShouldBe(CaseWriteResult.Ok);
        await using (var db = kit.Factory.CreateDbContext())
        {
            (await db.LinkEvents.CountAsync()).ShouldBe(0);
        }
    }

    [Fact(DisplayName = "Ограничения таблиц: уровень вне перечня, пустое название, повтор участника — не записать и в обход хранилища")]
    public async Task Table_constraints_hold()
    {
        var kit = await ArrangeAsync();
        var s = await SeedAsync(kit);
        var owner = InvestigationTestKit.Access(Owner, 3, 5);
        var (_, eventId) = await kit.Events.LinkAsync(Link(s.P1, s.P2, NewEvent("Встреча")), owner, Owner);

        await using var db = kit.Factory.CreateDbContext();
        var level = async () => await db.Database.ExecuteSqlAsync(
            $"UPDATE investigation.link_event_participant SET level = 4 WHERE event_id = {eventId}");
        await level.ShouldThrowAsync<DbException>();
        var title = async () => await db.Database.ExecuteSqlAsync($"UPDATE investigation.link_event SET title = '  ' WHERE id = {eventId}");
        await title.ShouldThrowAsync<DbException>();
        var duplicate = async () => await db.Database.ExecuteSqlAsync(
            $"INSERT INTO investigation.link_event_participant (event_id, person_id, level, in_search, classification, division_id, created_at) VALUES ({eventId}, {s.P1}, 1, true, 2, 5, now())");
        await duplicate.ShouldThrowAsync<DbException>();
    }

    private static LinkPersonsDraft Link(
        int personId, int otherId, LinkEventDraft? newEvent, string? role = null, string? otherRole = null, LinkParticipantDraft? contact = null) =>
        new(personId, otherId, null, newEvent, role, otherRole, contact ?? Contact);

    private static LinkEventDraft? NewEvent(string title) => new(LinkEventKind.Meeting, title, new DateOnly(2026, 10, 5), null);

    private static GetRelationGraphQuery.Handler Graph(Kit kit, AccessContext access) =>
        new(kit.Persons, kit.Requisites, kit.Cases, kit.Intersections, kit.Events, new ReferenceStore(kit.Factory), Provider(access));

    private static FindRelationPathQuery.Handler Path(Kit kit, AccessContext access) =>
        new(kit.Persons, kit.Requisites, kit.Cases, kit.Intersections, kit.Events, new ReferenceStore(kit.Factory), Provider(access));

    private static IAccessContextProvider Provider(AccessContext access)
    {
        var provider = Substitute.For<IAccessContextProvider>();
        provider.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(access);
        return provider;
    }

    // Дела: А-1 (гриф 2) и А-2 (гриф 3) — следователя; Б-1 (гриф 2) — коллеги; Д-6 — коллеги в подразделении 6; С-4 — гриф 4.
    private static async Task<Scene> SeedAsync(Kit kit)
    {
        var owner = InvestigationTestKit.Access(Owner, 9, 5);
        var colleague = InvestigationTestKit.Access(Colleague, 9, 5, 6);
        var caseA1 = (await kit.Cases.CreateAsync(InvestigationTestKit.Draft("А-1", 5, 2, Owner), owner)).CaseId;
        var caseA2 = (await kit.Cases.CreateAsync(InvestigationTestKit.Draft("А-2", 5, 3, Owner), owner)).CaseId;
        var caseB = (await kit.Cases.CreateAsync(InvestigationTestKit.Draft("Б-1", 5, 2, Colleague), colleague)).CaseId;
        var caseD = (await kit.Cases.CreateAsync(InvestigationTestKit.Draft("Д-6", 6, 2, Colleague), colleague)).CaseId;
        var caseS = (await kit.Cases.CreateAsync(InvestigationTestKit.Draft("С-4", 5, 4, Colleague), colleague)).CaseId;
        return new Scene(
            caseA1, caseA2, caseB,
            await PersonAsync(kit, owner, caseA1, "Объект Один"),
            await PersonAsync(kit, owner, caseA2, "Объект Два"),
            await PersonAsync(kit, colleague, caseB, "Объект Бэ"),
            await PersonAsync(kit, colleague, caseB, "Объект Бэ-два"),
            await PersonAsync(kit, colleague, caseD, "Объект Дэ"),
            await PersonAsync(kit, colleague, caseS, "Объект Эс"));
    }

    private static async Task<int> PersonAsync(Kit kit, AccessContext access, int caseId, string name)
    {
        var (result, id) = await kit.Persons.CreateAsync(new PersonDraft(caseId, name, false, null, null, PersonRole.Target), access);
        result.ShouldBe(PersonWriteResult.Ok);
        return id;
    }

    private async Task<Kit> ArrangeAsync()
    {
        var factory = new InvestigationContextFactory(_postgres.GetConnectionString());
        var core = new CoreContextFactory(_postgres.GetConnectionString());
        await InvestigationTestKit.MigrateAsync(factory);
        await InvestigationTestKit.AssignRolesAsync(factory,
            (Owner, InvestigationRole.Investigator), (Colleague, InvestigationRole.Investigator), (Outsider, InvestigationRole.Investigator),
            (Head, InvestigationRole.Head));
        var media = new MediaContextFactory(_postgres.GetConnectionString());
        await using (var mediaDb = media.CreateDbContext())
        {
            await mediaDb.Database.MigrateAsync();
        }

        var policy = new InvestigationAccessPolicy(factory);
        var roles = new UserRoleStore(core, factory);
        return new Kit(
            factory,
            InvestigationTestKit.CreateCaseStore(factory, core),
            InvestigationTestKit.CreatePersonStore(factory, core),
            new PersonRequisiteStore(factory, policy, roles),
            new IntersectionStore(factory, policy, roles, new MediaCatalog(media, policy)),
            new LinkEventStore(factory, policy, roles));
    }

    private sealed record Scene(int CaseA1, int CaseA2, int CaseB, int P1, int P2, int PB, int PB2, int PD, int PS);

    private sealed record Kit(
        InvestigationContextFactory Factory,
        CaseStore Cases,
        PersonStore Persons,
        PersonRequisiteStore Requisites,
        IntersectionStore Intersections,
        LinkEventStore Events);
}
