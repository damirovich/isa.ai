using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Data;
using ISC.AI.Modules.Media.Data.Entities;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Profile.Investigation.Application.Features.RelationGraph;
using ISC.AI.Profile.Investigation.Data;
using ISC.AI.Profile.Investigation.Domain.Entities;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Shouldly;
using Testcontainers.PostgreSql;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Пересечения между делами (ТФ-ПЕР-07, ТБ-084, ADR-0029) на настоящем Postgres: совпадения нормализованного
/// госномера, адреса и ФИО + даты рождения находятся с делом коллеги в пределах допуска; дело выше допуска и
/// чужого подразделения не раскрывается никак; совпадение внутри своего дела — не пересечение; признак «можно
/// открыть» следует ТФ-ДЕЛ-03; решение пишется только по видимому пересечению и перезаписывается повторным.
/// </summary>
public sealed class InvestigationIntersectionsTests : IAsyncLifetime
{
    private const int Owner = 10;
    private const int Colleague = 11;
    private const int Head = 20;

    private readonly PostgreSqlContainer _postgres = TestPostgres.Create();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "Пересечения: дело коллеги в пределах допуска — номер, реквизит, ответственный; выше допуска, чужое подразделение и своё дело — ничего")]
    public async Task Intersections_respect_clearance_and_division()
    {
        var kit = await ArrangeAsync();
        var scene = await SeedAsync(kit);

        // Следователь: допуск 3, подразделение 5 — видит дело коллеги, но не может открыть его по роли.
        var viewer = InvestigationTestKit.Access(Owner, 3, 5);
        var rows = (await kit.Intersections.FindForPersonAsync(scene.Target, viewer)).ShouldNotBeNull();

        rows.Select(r => r.OtherCaseId).Distinct().ShouldBe([scene.ColleagueCase]);

        var vehicle = rows.Single(r => r.Kind == IntersectionKind.Vehicle);
        vehicle.Key.ShouldBe("01KG123ABC");
        vehicle.OwnValue.ShouldBe("01 KG 123 ABC");
        vehicle.OtherCaseNumber.ShouldBe("Б-19/26");
        vehicle.ResponsibleUserId.ShouldBe(Colleague);
        vehicle.CanOpenCase.ShouldBeFalse();
        vehicle.Decision.ShouldBeNull();

        // Адрес строки сошёлся с местом жительства из анкеты фигуранта другого дела.
        rows.Single(r => r.Kind == IntersectionKind.Address).Key.ShouldBe("бишкек ул токтогула 1");

        // ФИО + дата рождения: однофамилец с другой датой пересечением не считается.
        var name = rows.Single(r => r.Kind == IntersectionKind.PersonName);
        name.OtherValue.ShouldBe("Семёнов Пётр, 01.02.1990");

        // Руководитель подразделения видит те же пересечения и может открыть дело (ТФ-ДЕЛ-03).
        var head = (await kit.Intersections.FindForPersonAsync(scene.Target, InvestigationTestKit.Access(Head, 3, 5))).ShouldNotBeNull();
        head.Count.ShouldBe(rows.Count);
        head.ShouldAllBe(r => r.CanOpenCase);

        // С широким допуском (4, подразделения 5 и 6) проявляются и скрытые дела — значит, скрывал именно floor.
        var wide = (await kit.Intersections.FindForPersonAsync(scene.Target, InvestigationTestKit.Access(Head, 4, 5, 6))).ShouldNotBeNull();
        wide.Select(r => r.OtherCaseId).Distinct().Order().ShouldBe(new[] { scene.ColleagueCase, scene.SecretCase, scene.OtherDivisionCase }.Order());

        // Фигурант недоступен по роли (чужой следователь без дела) — «не найден», а не пустой список.
        (await kit.Intersections.FindForPersonAsync(scene.Target, InvestigationTestKit.Access(Colleague, 3, 5))).ShouldBeNull();
    }

    [Fact(DisplayName = "Граф связей (ADR-0045): дело коллеги — узлом «чужое дело» без его фигурантов; центром — «не найден»; руководителю — обычное дело")]
    public async Task Relation_graph_shows_colleague_case_only_as_intersection()
    {
        var kit = await ArrangeAsync();
        var scene = await SeedAsync(kit);

        // Следователь, глубина 3: объект → своё дело → его фигуранты → их реквизиты и пересечения.
        var investigator = RelationGraph(kit, InvestigationTestKit.Access(Owner, 3, 5));
        var graph = (await investigator.Handle(
            new GetRelationGraphQuery(RelationNodeKind.Person, scene.Target, Depth: 3), CancellationToken.None)).Data.ShouldNotBeNull();

        var foreign = graph.Nodes.Where(n => n.Kind == RelationNodeKind.ForeignCase).ShouldHaveSingleItem();
        foreign.Key.ShouldBe(RelationNodeKeys.ForeignCase(scene.ColleagueCase));
        foreign.Title.ShouldBe("Дело № Б-19/26");
        foreign.EntityId.ShouldBeNull();
        foreign.ResponsibleUserId.ShouldBe(Colleague);

        // Фигуранты — только своего дела; дела коллеги узлом дела нет, скрытых дел (выше допуска, чужое
        // подразделение) — ни в каком виде (ТБ-084).
        graph.Nodes.Where(n => n.Kind == RelationNodeKind.Person)
            .ShouldAllBe(n => n.Subtitle!.EndsWith("дело № А-1/26", StringComparison.Ordinal));
        graph.Nodes.ShouldNotContain(n => n.Key == RelationNodeKeys.Case(scene.ColleagueCase));
        var hidden = new[] { scene.SecretCase, scene.OtherDivisionCase }
            .SelectMany(id => new[] { RelationNodeKeys.Case(id), RelationNodeKeys.ForeignCase(id) })
            .ToHashSet(StringComparer.Ordinal);
        graph.Nodes.ShouldNotContain(n => hidden.Contains(n.Key));

        // Один узел автомобиля: номер объекта и связи своего дела нормализуются в один ключ (ТО-мат-11).
        graph.Nodes.Where(n => n.Kind == RelationNodeKind.Vehicle).ShouldHaveSingleItem().Key
            .ShouldBe(RelationNodeKeys.Vehicle("01KG123ABC"));

        // Дело коллеги центром графа следователю недоступно — как и его карточка.
        (await investigator.Handle(new GetRelationGraphQuery(RelationNodeKind.Case, scene.ColleagueCase), CancellationToken.None))
            .StatusCode.ShouldBe(ResponseStatusCode.NotFound);

        // Руководитель подразделения видит дело коллеги и по роли — обычный узел дела, раскрывается (ТФ-ДЕЛ-03).
        var head = (await RelationGraph(kit, InvestigationTestKit.Access(Head, 3, 5)).Handle(
            new GetRelationGraphQuery(RelationNodeKind.Person, scene.Target, Depth: 1), CancellationToken.None)).Data.ShouldNotBeNull();
        head.Nodes.Where(n => n.Key == RelationNodeKeys.Case(scene.ColleagueCase)).ShouldHaveSingleItem().Expandable.ShouldBeTrue();
        head.Nodes.ShouldNotContain(n => n.Kind == RelationNodeKind.ForeignCase);
    }

    [Fact(DisplayName = "Граф связей v0.2 (ADR-0045 п. 10, 12): по госномеру, адресу и ФИО — только доступные фигуранты; «ё/е», регистр и знаки LIKE не мешают")]
    public async Task Relation_selections_respect_full_lattice()
    {
        var kit = await ArrangeAsync();
        var scene = await SeedAsync(kit);
        var investigator = InvestigationTestKit.Access(Owner, 3, 5);
        var head = InvestigationTestKit.Access(Head, 3, 5);

        // Госномер: следователю — только своё дело; руководителю — и дело коллеги; выше допуска и чужое подразделение — никому.
        (await kit.Persons.ListByPlateAsync("01KG123ABC", investigator)).Select(p => p.DisplayName)
            .ShouldBe(["Семенов Петр", "Связь"], ignoreOrder: true);
        (await kit.Persons.ListByPlateAsync("01KG123ABC", head)).Select(p => p.DisplayName)
            .ShouldBe(["Семенов Петр", "Связь", "Шторм", "Второй"], ignoreOrder: true);

        // Адрес: строкой адресов (вид — из строки) и местом жительства из анкеты (вид не задан).
        var residents = await kit.Persons.ListByAddressAsync("бишкек ул токтогула 1", head);
        residents.Single(r => r.Person.Id == scene.Target).Kind.ShouldBe(AddressKind.Residence);
        residents.Single(r => r.Person.DisplayName == "Шторм").Kind.ShouldBeNull();
        (await kit.Persons.ListByAddressAsync("бишкек ул токтогула 1", investigator)).ShouldHaveSingleItem().Person.Id.ShouldBe(scene.Target);

        // ФИО: регистр и «ё/е» не мешают; чужие дела — только в пределах роли и допуска.
        (await kit.Persons.SearchAsync("СЕМЁН", 20, investigator)).ShouldHaveSingleItem().Id.ShouldBe(scene.Target);
        (await kit.Persons.SearchAsync("семен", 20, head)).Select(p => p.DisplayName)
            .ShouldBe(["Семенов Петр", "Семенов Петр", "Семёнов Пётр"], ignoreOrder: true);
        (await kit.Persons.SearchAsync("семен", 1, head)).Count.ShouldBe(1);

        // «_» и «%» в запросе — буквальные: неэкранированный «_в» нашёл бы «Семенов».
        (await kit.Persons.SearchAsync("_в", 20, head)).ShouldBeEmpty();
        (await kit.Persons.SearchAsync("%", 20, head)).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Граф связей v0.2 (ADR-0045 п. 11): «вместе на материалах» — только действующие появления доступных фигурантов")]
    public async Task Co_appearances_respect_lattice_and_revocation()
    {
        var kit = await ArrangeAsync();
        var owner = InvestigationTestKit.Access(Owner, 9, 5);
        var colleague = InvestigationTestKit.Access(Colleague, 9, 5, 6);

        var ownCase = (await kit.Cases.CreateAsync(InvestigationTestKit.Draft("А-1/26", 5, 2, Owner), owner)).CaseId;
        var colleagueCase = (await kit.Cases.CreateAsync(InvestigationTestKit.Draft("Б-19/26", 5, 2, Colleague), colleague)).CaseId;
        var secretCase = (await kit.Cases.CreateAsync(InvestigationTestKit.Draft("С-7/26", 5, 4, Colleague), colleague)).CaseId;

        var target = await CreatePersonAsync(kit, owner, ownCase, "Объект", null);
        var partner = await CreatePersonAsync(kit, owner, ownCase, "Напарник", null);
        var revoked = await CreatePersonAsync(kit, owner, ownCase, "Отозванный", null);
        var colleaguePerson = await CreatePersonAsync(kit, colleague, colleagueCase, "Шторм", null);
        var secretPerson = await CreatePersonAsync(kit, colleague, secretCase, "Скрытый", null);

        const int FirstAsset = 600, SecondAsset = 601;
        await using (var db = kit.Factory.CreateDbContext())
        {
            var candidate = 1;
            var at = new DateTime(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc);
            var revokedAppearance = Appearance(revoked, ownCase, FirstAsset, 700, candidate++, 2, 5, at);
            revokedAppearance.Status = AppearanceStatus.Revoked;
            db.Appearances.AddRange(
                Appearance(target, ownCase, FirstAsset, 701, candidate++, 2, 5, at),
                Appearance(target, ownCase, SecondAsset, 702, candidate++, 2, 5, at),
                Appearance(partner, ownCase, FirstAsset, 703, candidate++, 2, 5, at),
                Appearance(partner, ownCase, SecondAsset, 704, candidate++, 2, 5, at),
                revokedAppearance,
                Appearance(colleaguePerson, colleagueCase, FirstAsset, 705, candidate++, 2, 5, at),
                Appearance(secretPerson, secretCase, FirstAsset, 706, candidate++, 4, 5, at));
            await db.SaveChangesAsync();
        }

        // Следователь: напарник на двух материалах; отозванное появление совместности не даёт; дело коллеги — не по роли,
        // секретное — не по допуску.
        var mine = await kit.Persons.ListCoAppearancesAsync(target, InvestigationTestKit.Access(Owner, 3, 5));
        mine.ShouldHaveSingleItem().Person.Id.ShouldBe(partner);
        mine[0].MaterialCount.ShouldBe(2);

        // Руководитель видит и фигуранта коллеги; секретный — по-прежнему нет.
        var headView = await kit.Persons.ListCoAppearancesAsync(target, InvestigationTestKit.Access(Head, 3, 5));
        headView.Select(r => r.Person.Id).ShouldBe([partner, colleaguePerson], ignoreOrder: true);

        // Недоступный исходный фигурант — пусто, а не чужие имена.
        (await kit.Persons.ListCoAppearancesAsync(secretPerson, InvestigationTestKit.Access(Head, 3, 5))).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Цепочка «как связаны» (ADR-0045 п. 12): руководителю — через общий номер или адрес за 2 шага; следователю цель недоступна — «не найден»")]
    public async Task Relation_path_follows_lattice()
    {
        var kit = await ArrangeAsync();
        var scene = await SeedAsync(kit);
        int shtorm;
        await using (var db = kit.Factory.CreateDbContext())
        {
            shtorm = await db.Persons.Where(p => p.DisplayName == "Шторм").Select(p => p.Id).SingleAsync();
        }

        var path = (await RelationPath(kit, InvestigationTestKit.Access(Head, 3, 5))
            .Handle(new FindRelationPathQuery(RelationNodeKind.Person, scene.Target, shtorm), CancellationToken.None)).Data.ShouldNotBeNull();
        path.Nodes[0].Key.ShouldBe(RelationNodeKeys.Person(scene.Target));
        path.Nodes[^1].Key.ShouldBe(RelationNodeKeys.Person(shtorm));
        path.Edges.Count.ShouldBe(2);
        path.Nodes[1].Kind.ShouldBeOneOf(RelationNodeKind.Vehicle, RelationNodeKind.Address);

        var denied = await RelationPath(kit, InvestigationTestKit.Access(Owner, 3, 5))
            .Handle(new FindRelationPathQuery(RelationNodeKind.Person, scene.Target, shtorm), CancellationToken.None);
        denied.StatusCode.ShouldBe(ResponseStatusCode.NotFound);
        denied.StatusMessage.ShouldBe(GetRelationGraphQuery.NotFound);
    }

    [Fact(DisplayName = "Решение по пересечению: пишется строкой своего дела и перезаписывается; по невидимому пересечению — NotFound")]
    public async Task Review_is_written_only_for_visible_intersection()
    {
        var kit = await ArrangeAsync();
        var scene = await SeedAsync(kit);
        var viewer = InvestigationTestKit.Access(Owner, 3, 5);

        (await kit.Intersections.ReviewAsync(scene.Target, IntersectionKind.Vehicle, "01KG123ABC", scene.ColleagueCase,
            IntersectionDecision.Confirmed, viewer)).ShouldBe(PersonWriteResult.Ok);
        (await kit.Intersections.ReviewAsync(scene.Target, IntersectionKind.Vehicle, "01KG123ABC", scene.ColleagueCase,
            IntersectionDecision.Rejected, viewer)).ShouldBe(PersonWriteResult.Ok);

        var row = (await kit.Intersections.FindForPersonAsync(scene.Target, viewer)).ShouldNotBeNull()
            .Single(r => r.Kind == IntersectionKind.Vehicle);
        row.Decision.ShouldBe(IntersectionDecision.Rejected);
        row.DecidedByUserId.ShouldBe(Owner);
        row.DecidedAt.ShouldNotBeNull();

        await using (var db = kit.Factory.CreateDbContext())
        {
            var stored = await db.IntersectionReviews.AsNoTracking().SingleAsync();
            stored.CaseId.ShouldBe(scene.OwnCase);
            stored.OtherCaseId.ShouldBe(scene.ColleagueCase);
            stored.Classification.ShouldBe<short>(2);
            stored.DivisionId.ShouldBe(5);
        }

        // Дело выше допуска, несуществующее дело, чужой ключ — один и тот же ответ, строк не прибавляется.
        (await kit.Intersections.ReviewAsync(scene.Target, IntersectionKind.Vehicle, "01KG123ABC", scene.SecretCase,
            IntersectionDecision.Confirmed, viewer)).ShouldBe(PersonWriteResult.NotFound);
        (await kit.Intersections.ReviewAsync(scene.Target, IntersectionKind.Vehicle, "01KG123ABC", 999_999,
            IntersectionDecision.Confirmed, viewer)).ShouldBe(PersonWriteResult.NotFound);
        (await kit.Intersections.ReviewAsync(scene.Target, IntersectionKind.Vehicle, "XXX", scene.ColleagueCase,
            IntersectionDecision.Confirmed, viewer)).ShouldBe(PersonWriteResult.NotFound);
        (await kit.Intersections.ReviewAsync(scene.Target, IntersectionKind.Vehicle, "01KG123ABC", scene.ColleagueCase,
            IntersectionDecision.Confirmed, InvestigationTestKit.Access(Colleague, 3, 5))).ShouldBe(PersonWriteResult.NotFound);

        await using (var db = kit.Factory.CreateDbContext())
        {
            (await db.IntersectionReviews.CountAsync()).ShouldBe(1);
        }

        // Удаление своего фигуранта уносит решение (каскад), чужое дело не затронуто.
        await using (var db = kit.Factory.CreateDbContext())
        {
            await db.Persons.Where(p => p.Id == scene.Target).ExecuteDeleteAsync();
            (await db.IntersectionReviews.CountAsync()).ShouldBe(0);
            (await db.Cases.CountAsync(c => c.Id == scene.ColleagueCase)).ShouldBe(1);
        }
    }

    [Fact(DisplayName = "Пересечение по лицу: объект на материале чужого дела и то же лицо у чужого фигуранта; выше допуска, чужое подразделение и своё дело — ничего")]
    public async Task Face_intersections_respect_clearance_and_own_case()
    {
        var kit = await ArrangeAsync();
        var owner = InvestigationTestKit.Access(Owner, 9, 5);
        var colleague = InvestigationTestKit.Access(Colleague, 9, 5, 6);

        var ownCase = (await kit.Cases.CreateAsync(InvestigationTestKit.Draft("А-1/26", 5, 2, Owner), owner)).CaseId;
        var colleagueCase = (await kit.Cases.CreateAsync(InvestigationTestKit.Draft("Б-19/26", 5, 2, Colleague), colleague)).CaseId;
        var secretCase = (await kit.Cases.CreateAsync(InvestigationTestKit.Draft("С-7/26", 5, 4, Colleague), colleague)).CaseId;
        var otherDivisionCase = (await kit.Cases.CreateAsync(InvestigationTestKit.Draft("Д-3/26", 6, 2, Colleague), colleague)).CaseId;

        var target = await CreatePersonAsync(kit, owner, ownCase, "Объект", null);
        var sameCaseLink = await CreatePersonAsync(kit, owner, ownCase, "Связь", null);
        var colleaguePerson = await CreatePersonAsync(kit, colleague, colleagueCase, "Шторм", null);
        var secretPerson = await CreatePersonAsync(kit, colleague, secretCase, "Скрытый", null);
        var otherDivisionPerson = await CreatePersonAsync(kit, colleague, otherDivisionCase, "Чужой", null);

        const int SharedAsset = 500, OwnOnlyAsset = 501;
        const int SharedFace = 900, OwnOnlyFace = 901;
        await using (var db = kit.Factory.CreateDbContext())
        {
            // Носитель 500 — в своём деле и (после дедупликации) в делах коллеги, секретном и чужого подразделения.
            foreach (var caseId in new[] { ownCase, colleagueCase, secretCase, otherDivisionCase })
            {
                db.CaseMediaLinks.Add(new CaseMediaLink { CaseId = caseId, MediaAssetId = SharedAsset });
            }

            db.CaseMediaLinks.Add(new CaseMediaLink { CaseId = ownCase, MediaAssetId = OwnOnlyAsset });

            var candidate = 1;
            db.Appearances.AddRange(
                Appearance(target, ownCase, SharedAsset, SharedFace, candidate++, 2, 5, new DateTime(2026, 9, 20, 10, 0, 0, DateTimeKind.Utc)),
                Appearance(target, ownCase, OwnOnlyAsset, OwnOnlyFace, candidate++, 2, 5, new DateTime(2026, 9, 21, 10, 0, 0, DateTimeKind.Utc)),
                // То же лицо: у связи СВОЕГО дела (не пересечение), у фигуранта коллеги, у скрытых.
                Appearance(sameCaseLink, ownCase, SharedAsset, SharedFace, candidate++, 2, 5, DateTime.UtcNow),
                Appearance(colleaguePerson, colleagueCase, SharedAsset, SharedFace, candidate++, 2, 5, DateTime.UtcNow),
                Appearance(secretPerson, secretCase, SharedAsset, SharedFace, candidate++, 4, 5, DateTime.UtcNow),
                Appearance(otherDivisionPerson, otherDivisionCase, SharedAsset, SharedFace, candidate++, 2, 6, DateTime.UtcNow));
            await db.SaveChangesAsync();
        }

        var viewer = InvestigationTestKit.Access(Owner, 3, 5);
        var rows = (await kit.Intersections.FindForPersonAsync(target, viewer)).ShouldNotBeNull()
            .Where(r => r.Kind == IntersectionKind.Face).ToList();

        rows.Select(r => r.OtherCaseId).Distinct().ShouldBe([colleagueCase]);
        rows.Select(r => r.Key).Order().ShouldBe(["face:material", "face:shared"]);
        rows.Single(r => r.Key == "face:material").OtherValue.ShouldContain("появлений 1, последнее 20.09.2026");
        rows.Single(r => r.Key == "face:shared").OtherValue.ShouldContain("совпавших появлений объекта 1");
        rows.ShouldAllBe(r => r.OwnValue.StartsWith("лицо объекта") && r.ResponsibleUserId == Colleague && !r.CanOpenCase);
        rows.ShouldAllBe(r => !r.OtherValue.Contains("Шторм")); // имя чужого фигуранта не раскрывается (ТБ-084)

        // С допуском 4 и подразделениями 5, 6 проявляются и скрытые дела — значит, скрывал именно floor.
        var wide = (await kit.Intersections.FindForPersonAsync(target, InvestigationTestKit.Access(Owner, 4, 5, 6))).ShouldNotBeNull()
            .Where(r => r.Kind == IntersectionKind.Face).Select(r => r.OtherCaseId).Distinct().Order();
        wide.ShouldBe(new[] { colleagueCase, secretCase, otherDivisionCase }.Order());

        // Решение по пересечению по лицу — как по остальным; по невидимому делу — «не найдено».
        (await kit.Intersections.ReviewAsync(target, IntersectionKind.Face, "face:shared", colleagueCase, IntersectionDecision.Confirmed, viewer))
            .ShouldBe(PersonWriteResult.Ok);
        (await kit.Intersections.ReviewAsync(target, IntersectionKind.Face, "face:shared", secretCase, IntersectionDecision.Confirmed, viewer))
            .ShouldBe(PersonWriteResult.NotFound);
        (await kit.Intersections.FindForPersonAsync(target, viewer)).ShouldNotBeNull()
            .Single(r => r.Kind == IntersectionKind.Face && r.Key == "face:shared").Decision.ShouldBe(IntersectionDecision.Confirmed);

        // Со стороны фигуранта коллеги — зеркальное пересечение с делом объекта.
        var mirrored = (await kit.Intersections.FindForPersonAsync(colleaguePerson, InvestigationTestKit.Access(Colleague, 3, 5))).ShouldNotBeNull()
            .Where(r => r.Kind == IntersectionKind.Face).ToList();
        mirrored.ShouldContain(r => r.Key == "face:shared" && r.OtherCaseId == ownCase);
    }

    [Fact(DisplayName = "Отзыв появления (ADR-0034): эксперт и верификатор не отзывают; отозванное остаётся в истории, но уходит из пересечений, подсказок и счётчика; лицо можно подтвердить заново")]
    public async Task Revoked_appearance_leaves_intersections_but_stays_in_history()
    {
        var kit = await ArrangeAsync();
        var owner = InvestigationTestKit.Access(Owner, 9, 5);
        var colleague = InvestigationTestKit.Access(Colleague, 9, 5);

        var ownCase = (await kit.Cases.CreateAsync(InvestigationTestKit.Draft("А-1/26", 5, 2, Owner), owner)).CaseId;
        var colleagueCase = (await kit.Cases.CreateAsync(InvestigationTestKit.Draft("Б-19/26", 5, 2, Colleague), colleague)).CaseId;
        var target = await CreatePersonAsync(kit, owner, ownCase, "Объект", null);
        var colleaguePerson = await CreatePersonAsync(kit, colleague, colleagueCase, "Шторм", null);

        const int Asset = 500, Face = 900;
        int ownAppearance;
        await using (var db = kit.Factory.CreateDbContext())
        {
            db.CaseMediaLinks.AddRange(
                new CaseMediaLink { CaseId = ownCase, MediaAssetId = Asset },
                new CaseMediaLink { CaseId = colleagueCase, MediaAssetId = Asset });
            var own = Appearance(target, ownCase, Asset, Face, 1, 2, 5, DateTime.UtcNow);
            db.Appearances.AddRange(own, Appearance(colleaguePerson, colleagueCase, Asset, Face, 2, 2, 5, DateTime.UtcNow));
            await db.SaveChangesAsync();
            ownAppearance = own.Id;
        }

        var viewer = InvestigationTestKit.Access(Owner, 3, 5);
        (await kit.Intersections.FindForPersonAsync(target, viewer)).ShouldNotBeNull().ShouldContain(r => r.Kind == IntersectionKind.Face);

        // Правило двух лиц для отзыва: эксперт (1) и верификатор (2) этого появления его не отзывают — даже видя дело.
        await InvestigationTestKit.AssignRolesAsync(kit.Factory, (1, InvestigationRole.FaceExpert), (2, InvestigationRole.Verifier));
        (await kit.Persons.RevokeAppearanceAsync(ownAppearance, "ошибка эксперта", InvestigationTestKit.Access(1, 9, 5)))
            .ShouldBe(AppearanceRevokeResult.OwnDecision);
        (await kit.Persons.RevokeAppearanceAsync(ownAppearance, "ошибка эксперта", InvestigationTestKit.Access(2, 9, 5)))
            .ShouldBe(AppearanceRevokeResult.OwnDecision);

        // Выше допуска не видно — «не найдено», а не «нельзя» (ТБ-021).
        (await kit.Persons.RevokeAppearanceAsync(ownAppearance, "проверка", InvestigationTestKit.Access(Owner, 1, 5)))
            .ShouldBe(AppearanceRevokeResult.NotFound);

        (await kit.Persons.RevokeAppearanceAsync(ownAppearance, "  на кадре другой человек  ", owner)).ShouldBe(AppearanceRevokeResult.Ok);
        (await kit.Persons.RevokeAppearanceAsync(ownAppearance, "повтор", owner)).ShouldBe(AppearanceRevokeResult.AlreadyRevoked);

        // История: строка на месте, с причиной и отозвавшим.
        var history = (await kit.Persons.ListAppearancesAsync(target, owner)).ShouldHaveSingleItem();
        history.IsRevoked.ShouldBeTrue();
        history.RevokeReason.ShouldBe("на кадре другой человек");
        history.RevokedByUserId.ShouldBe(Owner);

        // Пересечения, подсказка «уже подтверждено у …» и счётчик — без отозванного.
        (await kit.Intersections.FindForPersonAsync(target, viewer)).ShouldNotBeNull().ShouldNotContain(r => r.Kind == IntersectionKind.Face);
        (await kit.Intersections.FindForPersonAsync(colleaguePerson, InvestigationTestKit.Access(Colleague, 3, 5))).ShouldNotBeNull()
            // У коллеги своё действующее появление на общем материале — «на материале» законно остаётся; «то же лицо у
            // фигуранта» ушло вместе с отозванным появлением.
            .ShouldNotContain(r => r.Kind == IntersectionKind.Face && r.Key == "face:shared" && r.OtherCaseId == ownCase);
        (await kit.Persons.ListPersonsConfirmedOnFaceAsync(ownCase, Face, owner)).ShouldBeEmpty();
        (await kit.Persons.GetAsync(target, owner)).ShouldNotBeNull().AppearanceCount.ShouldBe(0);

        // Новое независимое подтверждение того же лица — новое появление, а не «уже есть».
        var again = await kit.Persons.AddAppearanceAsync(new AppearanceDraft(
            target, ownCase, Asset, Face, null, null, 7, 77, 0.9, DateTime.UtcNow, 3, 4, 2, 5));
        again.ShouldNotBe(ownAppearance);
        (await kit.Persons.ListPersonsConfirmedOnFaceAsync(ownCase, Face, owner)).ShouldBe([target]);
    }

    [Fact(DisplayName = "Пересечение по лицу: тот же файл в деле с другим грифом (отдельный носитель, тот же хеш) — находится в пределах допуска")]
    public async Task Face_intersection_finds_same_file_uploaded_under_other_classification()
    {
        var kit = await ArrangeAsync();
        var owner = InvestigationTestKit.Access(Owner, 9, 5);
        var colleague = InvestigationTestKit.Access(Colleague, 9, 5);

        // Своё дело — ДСП (1), дело коллеги — «Секретно» (3), скрытое — выше допуска просмотра (4).
        var ownCase = (await kit.Cases.CreateAsync(InvestigationTestKit.Draft("978", 5, 1, Owner), owner)).CaseId;
        var secretCase = (await kit.Cases.CreateAsync(InvestigationTestKit.Draft("312312312", 5, 3, Colleague), colleague)).CaseId;
        var hiddenCase = (await kit.Cases.CreateAsync(InvestigationTestKit.Draft("ОВ-1/26", 5, 4, Colleague), colleague)).CaseId;
        var target = await CreatePersonAsync(kit, owner, ownCase, "Объект", null);

        // Один снимок, загруженный в три дела разного грифа, — три носителя с одинаковым хешем (ТБ-070).
        var ownAsset = await MediaAssetAsync(kit, "SAMEPHOTO", 1, 5);
        var secretCopy = await MediaAssetAsync(kit, "SAMEPHOTO", 3, 5);
        var hiddenCopy = await MediaAssetAsync(kit, "SAMEPHOTO", 4, 5);
        var unrelated = await MediaAssetAsync(kit, "OTHERPHOTO", 3, 5);
        await using (var db = kit.Factory.CreateDbContext())
        {
            db.CaseMediaLinks.AddRange(
                new CaseMediaLink { CaseId = ownCase, MediaAssetId = ownAsset },
                new CaseMediaLink { CaseId = secretCase, MediaAssetId = secretCopy },
                new CaseMediaLink { CaseId = secretCase, MediaAssetId = unrelated },
                new CaseMediaLink { CaseId = hiddenCase, MediaAssetId = hiddenCopy });
            db.Appearances.Add(Appearance(target, ownCase, ownAsset, 900, 1, 1, 5, new DateTime(2026, 9, 18, 10, 0, 0, DateTimeKind.Utc)));
            await db.SaveChangesAsync();
        }

        // Допуск 3: копия в секретном деле видна — пересечение есть; дело с грифом 4 не проявляется.
        var viewer = InvestigationTestKit.Access(Owner, 3, 5);
        var rows = (await kit.Intersections.FindForPersonAsync(target, viewer)).ShouldNotBeNull()
            .Where(r => r.Kind == IntersectionKind.Face).ToList();
        var row = rows.ShouldHaveSingleItem();
        row.Key.ShouldBe("face:material");
        row.OtherCaseId.ShouldBe(secretCase);
        row.OtherValue.ShouldContain("появлений 1, последнее 18.09.2026");

        // Допуск 1: копия и дело «Секретно» вне допуска — ничего, как будто их нет.
        (await kit.Intersections.FindForPersonAsync(target, InvestigationTestKit.Access(Owner, 1, 5))).ShouldNotBeNull()
            .ShouldNotContain(r => r.Kind == IntersectionKind.Face);

        // Допуск 4 открывает и скрытое дело — значит, скрывал именно floor.
        (await kit.Intersections.FindForPersonAsync(target, InvestigationTestKit.Access(Owner, 4, 5))).ShouldNotBeNull()
            .Where(r => r.Kind == IntersectionKind.Face).Select(r => r.OtherCaseId).Order()
            .ShouldBe(new[] { secretCase, hiddenCase }.Order());

        // Решение по такому пересечению принимается, как по остальным.
        (await kit.Intersections.ReviewAsync(target, IntersectionKind.Face, "face:material", secretCase, IntersectionDecision.Confirmed, viewer))
            .ShouldBe(PersonWriteResult.Ok);
    }

    private static async Task<int> MediaAssetAsync(Kit kit, string hash, short classification, int divisionId)
    {
        await using var db = kit.Media.CreateDbContext();
        var asset = new MediaAsset
        {
            Kind = MediaKind.Image,
            OriginalFileName = "photo.jpg",
            StoredFileName = Guid.NewGuid().ToString("N") + ".jpg",
            ContentType = "image/jpeg",
            ContentHash = hash,
            ByteSize = 1,
            Classification = classification,
            DivisionId = divisionId,
            IndexStatus = MediaIndexStatus.Indexed,
        };
        db.Assets.Add(asset);
        await db.SaveChangesAsync();
        return asset.Id;
    }

    private static Appearance Appearance(
        int personId, int caseId, int assetId, int faceId, int candidateId, short classification, int divisionId, DateTime confirmedAtUtc) => new()
    {
        PersonId = personId,
        CaseId = caseId,
        MediaAssetId = assetId,
        MediaFaceId = faceId,
        SearchSessionId = 1,
        CandidateId = candidateId,
        Similarity = 0.8,
        ConfirmedAtUtc = confirmedAtUtc,
        ExpertUserId = 1,
        VerifierUserId = 2,
        Classification = classification,
        DivisionId = divisionId,
    };

    private static async Task<Scene> SeedAsync(Kit kit)
    {
        var owner = InvestigationTestKit.Access(Owner, 9, 5);
        var colleague = InvestigationTestKit.Access(Colleague, 9, 5, 6);

        var ownCase = (await kit.Cases.CreateAsync(InvestigationTestKit.Draft("А-1/26", 5, 2, Owner), owner)).CaseId;
        var colleagueCase = (await kit.Cases.CreateAsync(InvestigationTestKit.Draft("Б-19/26", 5, 2, Colleague), colleague)).CaseId;
        var secretCase = (await kit.Cases.CreateAsync(InvestigationTestKit.Draft("С-7/26", 5, 4, Colleague), colleague)).CaseId;
        var otherDivisionCase = (await kit.Cases.CreateAsync(InvestigationTestKit.Draft("Д-3/26", 6, 2, Colleague), colleague)).CaseId;

        var birth = new DateOnly(1990, 2, 1);
        var target = await CreatePersonAsync(kit, owner, ownCase, "Семенов Петр", new PersonQuestionnaire(BirthDate: birth));
        await SaveVehicleAsync(kit, owner, target, "01 KG 123 ABC");
        (await kit.Requisites.SaveAddressAsync(target, null, new PersonAddressDraft(AddressKind.Residence, "Бишкек, улица Токтогула 1", null), owner))
            .Result.ShouldBe(PersonWriteResult.Ok);

        // Совпадение внутри своего дела — не пересечение.
        var sameCase = await CreatePersonAsync(kit, owner, ownCase, "Связь", null);
        await SaveVehicleAsync(kit, owner, sameCase, "01KG123ABC");

        // Дело коллеги: два фигуранта с тем же номером (одна строка), место жительства, тёзка с той же датой и с другой.
        var shtorm = await CreatePersonAsync(kit, colleague, colleagueCase, "Шторм",
            new PersonQuestionnaire(Residence: "г. Бишкек, ул. Токтогула, д. 1"));
        await SaveVehicleAsync(kit, colleague, shtorm, "01kg123авс");
        var second = await CreatePersonAsync(kit, colleague, colleagueCase, "Второй", null);
        await SaveVehicleAsync(kit, colleague, second, "01-KG-123-ABC");
        await CreatePersonAsync(kit, colleague, colleagueCase, "Семёнов Пётр", new PersonQuestionnaire(BirthDate: birth));
        await CreatePersonAsync(kit, colleague, colleagueCase, "Семенов Петр", new PersonQuestionnaire(BirthDate: new DateOnly(1985, 5, 5)));

        // Выше допуска и чужое подразделение — те же реквизиты.
        foreach (var hidden in new[] { secretCase, otherDivisionCase })
        {
            var person = await CreatePersonAsync(kit, colleague, hidden, "Скрытый", new PersonQuestionnaire(BirthDate: birth));
            await SaveVehicleAsync(kit, colleague, person, "01KG123ABC");
        }

        return new Scene(ownCase, colleagueCase, secretCase, otherDivisionCase, target);
    }

    // Граф связей настоящими хранилищами под решёткой; допуск субъекта — через подменённого провайдера.
    private static GetRelationGraphQuery.Handler RelationGraph(Kit kit, AccessContext access)
    {
        var provider = Substitute.For<IAccessContextProvider>();
        provider.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(access);
        return new GetRelationGraphQuery.Handler(
            kit.Persons, kit.Requisites, kit.Cases, kit.Intersections, new ReferenceStore(kit.Factory), provider);
    }

    // Цепочка «как связаны» настоящими хранилищами под решёткой.
    private static FindRelationPathQuery.Handler RelationPath(Kit kit, AccessContext access)
    {
        var provider = Substitute.For<IAccessContextProvider>();
        provider.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(access);
        return new FindRelationPathQuery.Handler(
            kit.Persons, kit.Requisites, kit.Cases, kit.Intersections, new ReferenceStore(kit.Factory), provider);
    }

    [Fact(DisplayName = "ADR-0055: пересечение по телефону — разные написания одного номера сходятся; своё дело, выше допуска и чужое подразделение — нет; узел номера в графе и владельцы номера — под решёткой")]
    public async Task Phone_intersections_respect_lattice()
    {
        var kit = await ArrangeAsync();
        var scene = await SeedAsync(kit);
        var owner = InvestigationTestKit.Access(Owner, 9, 5);
        var colleague = InvestigationTestKit.Access(Colleague, 9, 5, 6);

        await SavePhoneAsync(kit, owner, scene.Target, "0555 123 456");
        var sameCase = await CreatePersonAsync(kit, owner, scene.OwnCase, "Сосед", null);
        await SavePhoneAsync(kit, owner, sameCase, "+996555123456"); // своё дело — не пересечение
        var caller = await CreatePersonAsync(kit, colleague, scene.ColleagueCase, "Абонент", null);
        await SavePhoneAsync(kit, colleague, caller, "+996 (555) 12-34-56");
        await SavePhoneAsync(kit, colleague, caller, "0555 999 999"); // иной номер — не совпадает
        foreach (var hidden in new[] { scene.SecretCase, scene.OtherDivisionCase })
        {
            await SavePhoneAsync(kit, colleague, await CreatePersonAsync(kit, colleague, hidden, "Скрытый абонент", null), "555 123 456");
        }

        var investigator = InvestigationTestKit.Access(Owner, 3, 5);
        var phone = (await kit.Intersections.FindForPersonAsync(scene.Target, investigator)).ShouldNotBeNull()
            .Where(r => r.Kind == IntersectionKind.Phone).ShouldHaveSingleItem();
        phone.Key.ShouldBe("996555123456");
        phone.OwnValue.ShouldBe("0555 123 456");
        phone.OtherValue.ShouldBe("+996 (555) 12-34-56");
        phone.OtherCaseId.ShouldBe(scene.ColleagueCase);
        phone.CanOpenCase.ShouldBeFalse();

        // Решение по пересечению — как у остальных видов; по скрытому делу — «не найден» (не оракул).
        (await kit.Intersections.ReviewAsync(scene.Target, IntersectionKind.Phone, "996555123456", scene.ColleagueCase,
            IntersectionDecision.Confirmed, investigator)).ShouldBe(PersonWriteResult.Ok);
        (await kit.Intersections.ReviewAsync(scene.Target, IntersectionKind.Phone, "996555123456", scene.SecretCase,
            IntersectionDecision.Confirmed, investigator)).ShouldBe(PersonWriteResult.NotFound);
        (await kit.Intersections.FindForPersonAsync(scene.Target, investigator)).ShouldNotBeNull()
            .Single(r => r.Kind == IntersectionKind.Phone).Decision.ShouldBe(IntersectionDecision.Confirmed);

        // С широким допуском проявляются скрытые дела — значит, скрывал именно floor (GATE-7).
        (await kit.Intersections.FindForPersonAsync(scene.Target, InvestigationTestKit.Access(Head, 4, 5, 6))).ShouldNotBeNull()
            .Where(r => r.Kind == IntersectionKind.Phone).Select(r => r.OtherCaseId).Order()
            .ShouldBe(new[] { scene.ColleagueCase, scene.SecretCase, scene.OtherDivisionCase }.Order());

        // Владельцы номера: следователю — своё дело; руководителю — и дело коллеги; скрытые — никому в пределах допуска 3.
        (await kit.Persons.ListByPhoneAsync("996555123456", investigator)).Select(p => p.Id).ShouldBe([scene.Target, sameCase], ignoreOrder: true);
        (await kit.Persons.ListByPhoneAsync("996555123456", InvestigationTestKit.Access(Head, 3, 5))).Select(p => p.Id)
            .ShouldBe([scene.Target, sameCase, caller], ignoreOrder: true);
        (await kit.Persons.ListByPhoneAsync("99655512345", investigator)).ShouldBeEmpty(); // только точное совпадение

        // Граф: один узел номера у объекта; пересечение — ребром от узла номера к «чужому делу».
        var graph = (await RelationGraph(kit, investigator).Handle(
            new GetRelationGraphQuery(RelationNodeKind.Person, scene.Target, Depth: 1), CancellationToken.None)).Data.ShouldNotBeNull();
        var phoneNode = graph.Nodes.Where(n => n.Kind == RelationNodeKind.Phone).ShouldHaveSingleItem();
        phoneNode.Key.ShouldBe(RelationNodeKeys.Phone("996555123456"));
        graph.Edges.ShouldContain(e => e.FromKey == phoneNode.Key && e.ToKey == RelationNodeKeys.ForeignCase(scene.ColleagueCase));
    }

    private static async Task SavePhoneAsync(Kit kit, AccessContext access, int personId, string number) =>
        (await kit.Requisites.SavePhoneAsync(personId, null, new PersonPhoneDraft(PhoneKind.Mobile, number, null), access))
            .Result.ShouldBe(PersonWriteResult.Ok);

    private static async Task<int> CreatePersonAsync(Kit kit, ISC.AI.Abstractions.Security.AccessContext access, int caseId, string name, PersonQuestionnaire? questionnaire)
    {
        var (result, id) = await kit.Persons.CreateAsync(
            new PersonDraft(caseId, name, false, null, null, PersonRole.Target, questionnaire), access);
        result.ShouldBe(PersonWriteResult.Ok);
        return id;
    }

    private static async Task SaveVehicleAsync(Kit kit, ISC.AI.Abstractions.Security.AccessContext access, int personId, string plate) =>
        (await kit.Requisites.SaveVehicleAsync(personId, null, new PersonVehicleDraft(plate, null, null, null, null), access))
            .Result.ShouldBe(PersonWriteResult.Ok);

    private async Task<Kit> ArrangeAsync()
    {
        var factory = new InvestigationContextFactory(_postgres.GetConnectionString());
        var core = new CoreContextFactory(_postgres.GetConnectionString());
        await InvestigationTestKit.MigrateAsync(factory);
        await InvestigationTestKit.AssignRolesAsync(factory,
            (Owner, InvestigationRole.Investigator), (Colleague, InvestigationRole.Investigator), (Head, InvestigationRole.Head));
        var media = new MediaContextFactory(_postgres.GetConnectionString());
        await using (var mediaDb = media.CreateDbContext())
        {
            await mediaDb.Database.MigrateAsync();
        }

        var policy = new InvestigationAccessPolicy(factory);
        var roles = new UserRoleStore(core, factory);
        return new Kit(
            factory,
            media,
            InvestigationTestKit.CreateCaseStore(factory, core),
            InvestigationTestKit.CreatePersonStore(factory, core),
            new PersonRequisiteStore(factory, policy, roles),
            new IntersectionStore(factory, policy, roles, new MediaCatalog(media, policy)));
    }

    private sealed record Scene(int OwnCase, int ColleagueCase, int SecretCase, int OtherDivisionCase, int Target);

    private sealed record Kit(
        InvestigationContextFactory Factory,
        MediaContextFactory Media,
        CaseStore Cases,
        PersonStore Persons,
        PersonRequisiteStore Requisites,
        IntersectionStore Intersections);
}
