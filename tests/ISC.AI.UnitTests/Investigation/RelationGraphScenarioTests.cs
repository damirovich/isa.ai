using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Application.Features.RelationGraph;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Investigation;

/// <summary>
/// Граф связей (ТФ-ПЕР-10, ADR-0045) без БД: строится только из хранилищ под решёткой; общие узлы автомобиля и адреса
/// по нормализованному ключу (ТО-мат-11); чужое дело — как в пересечениях, без идентификатора и раскрытия (ТБ-084);
/// отклонённые пересечения не рисуются; предохранитель числа узлов; раскрытие автомобиля и адреса — только через
/// фигуранта, у которого этот реквизит есть; совместные появления; цепочка «как связаны»; поиск и выгрузка; в журнал —
/// только идентификаторы (ТБ-032).
/// </summary>
public sealed class RelationGraphScenarioTests
{
    private const int CaseId = 3;
    private const int OtherCaseId = 5;
    private const int TargetId = 10;
    private const int BrotherId = 11;
    private const int OtherId = 12;
    private const int StrangerId = 30;
    private const int LonerId = 40;
    private const int BrotherLinkType = 7;
    private const string Residence = "г. Бишкек, ул. Ленина 1";
    private const string Plate = "01 KG 123 ABC";

    private static readonly AccessContext Access = new("42", MaxClassification: 2, AllowedDivisions: [5]);
    private static readonly string PlateKey = RequisiteNormalizer.Plate(Plate)!;
    private static readonly string HomeKey = RequisiteNormalizer.Address(Residence)!;

    private readonly IPersonStore _persons = Substitute.For<IPersonStore>();
    private readonly IPersonRequisiteStore _requisites = Substitute.For<IPersonRequisiteStore>();
    private readonly ICaseStore _cases = Substitute.For<ICaseStore>();
    private readonly IIntersectionStore _intersections = Substitute.For<IIntersectionStore>();
    private readonly ILinkEventStore _events = Substitute.For<ILinkEventStore>();
    private readonly IReferenceStore _references = Substitute.For<IReferenceStore>();
    private readonly IAccessContextProvider _access = Substitute.For<IAccessContextProvider>();

    private readonly PersonRow _target;
    private readonly PersonRow _brother;
    private readonly PersonRow _other;
    private readonly PersonRow _stranger;
    private readonly PersonRow _loner;

    public RelationGraphScenarioTests()
    {
        _access.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(Access);
        _references.ListAsync(ReferenceKind.LinkType, Arg.Any<CancellationToken>())
            .Returns([new ReferenceItemRow(BrotherLinkType, ReferenceKind.LinkType, "брат", null, 1, IsActive: true)]);

        _target = Person(TargetId, "Иванов Иван", PersonRole.Target, CaseId,
            questionnaire: new PersonQuestionnaire(Residence: Residence, Alias: "Шторм"), classification: 2);
        _brother = Person(BrotherId, "Иванов Пётр", PersonRole.Link, CaseId, linkedTo: TargetId, linkType: BrotherLinkType);
        _other = Person(OtherId, "Сидоров", PersonRole.Other, CaseId);
        _stranger = Person(StrangerId, "Петров", PersonRole.Target, OtherCaseId);
        _loner = Person(LonerId, "Одиночкин", PersonRole.Target, OtherCaseId);

        foreach (var person in new[] { _target, _brother, _other, _stranger, _loner })
        {
            _persons.GetAsync(person.Id, Access, Arg.Any<CancellationToken>()).Returns(person);
            _persons.ListCoAppearancesAsync(person.Id, Access, Arg.Any<CancellationToken>()).Returns([]);
            _intersections.FindForPersonAsync(person.Id, Access, Arg.Any<CancellationToken>()).Returns([]);
            _events.ListForPersonAsync(person.Id, Access, Arg.Any<CancellationToken>()).Returns([]);
            _requisites.GetAsync(person.Id, Access, Arg.Any<CancellationToken>()).Returns(new PersonRequisites([], [], []));
        }

        _persons.ListByCaseAsync(CaseId, Access, Arg.Any<CancellationToken>()).Returns([_target, _brother, _other]);
        _persons.ListByCaseAsync(OtherCaseId, Access, Arg.Any<CancellationToken>()).Returns([_stranger, _loner]);
        _cases.GetAsync(CaseId, Access, Arg.Any<CancellationToken>()).Returns(Case(CaseId, "12-2026"));
        _cases.GetAsync(OtherCaseId, Access, Arg.Any<CancellationToken>()).Returns(Case(OtherCaseId, "5-2026"));
        _cases.GetAsync(21, Access, Arg.Any<CancellationToken>()).Returns(Case(21, "21/26", classification: 1));

        // У объекта и брата — один и тот же автомобиль, записанный по-разному; адрес проживания объекта совпадает с анкетой.
        _requisites.GetAsync(TargetId, Access, Arg.Any<CancellationToken>()).Returns(new PersonRequisites(
            [
                new PersonAddressRow(1, TargetId, AddressKind.Residence, Residence, null),
                new PersonAddressRow(2, TargetId, AddressKind.Work, "ул. Токтогула 5", null),
            ],
            [new PersonVehicleRow(1, TargetId, Plate, "Toyota", "Camry", "белый", null)],
            []));
        _requisites.GetAsync(BrotherId, Access, Arg.Any<CancellationToken>()).Returns(new PersonRequisites(
            [], [new PersonVehicleRow(2, BrotherId, "01kg123abc", "Toyota", null, null, null)], []));

        _intersections.FindForPersonAsync(TargetId, Access, Arg.Any<CancellationToken>()).Returns(
        [
            Match(IntersectionKind.Vehicle, PlateKey, otherCaseId: 20, canOpen: false),
            Match(IntersectionKind.Address, HomeKey, otherCaseId: 21, canOpen: true),
            Match(IntersectionKind.PersonName, "иванов иван", otherCaseId: 22, canOpen: false, IntersectionDecision.Rejected),
            Match(IntersectionKind.Face, "face", otherCaseId: 23, canOpen: false, IntersectionDecision.Confirmed),
        ]);
    }

    [Fact(DisplayName = "ТФ-ПЕР-10: недоступный центр — «не найден», хранилища реквизитов и пересечений не читаются (ТБ-021)")]
    public async Task Inaccessible_center_is_not_found()
    {
        var response = await Handle(new GetRelationGraphQuery(RelationNodeKind.Person, 99));

        response.StatusCode.ShouldBe(ResponseStatusCode.NotFound);
        response.StatusMessage.ShouldBe(GetRelationGraphQuery.NotFound);
        await _requisites.DidNotReceiveWithAnyArgs().GetAsync(default, default!, default);
        await _intersections.DidNotReceiveWithAnyArgs().FindForPersonAsync(default, default!, default);
    }

    [Fact(DisplayName = "ТФ-ПЕР-10: фигурант в центре — дело, связь с типом, транспорт, адреса и пересечения; глубина 1 соседей не раскрывает")]
    public async Task Person_center_shows_case_links_requisites_and_intersections()
    {
        var graph = (await Handle(new GetRelationGraphQuery(RelationNodeKind.Person, TargetId, Depth: 1))).Data.ShouldNotBeNull();

        var center = graph.Nodes[0];
        center.Key.ShouldBe(RelationNodeKeys.Person(TargetId));
        center.Expanded.ShouldBeTrue();
        center.Subtitle.ShouldBe("Объект · «Шторм» · дело № 12-2026");
        center.Classification.ShouldBe<short?>(2);

        Node(graph, RelationNodeKeys.Case(CaseId)).Title.ShouldBe("Дело № 12-2026");
        var brother = Node(graph, RelationNodeKeys.Person(BrotherId));
        brother.Expanded.ShouldBeFalse();
        brother.ParentKey.ShouldBe(center.Key);
        Edge(graph, brother.Key, center.Key).Label.ShouldBe("брат");
        graph.Nodes.ShouldNotContain(n => n.Key == RelationNodeKeys.Person(OtherId)); // не связь объекта

        // Место жительства из анкеты и строка адреса проживания — один узел и одно ребро.
        var home = Node(graph, RelationNodeKeys.Address(HomeKey));
        graph.Edges.Count(e => e.ToKey == home.Key && e.Kind == RelationEdgeKind.Requisite).ShouldBe(1);
        graph.Edges.ShouldNotContain(e => e.Label == "жительство (анкета)");

        // Реквизит несёт своего фигуранта и его гриф — через него сервер проверит раскрытие (ADR-0045 п. 10).
        var vehicle = Node(graph, RelationNodeKeys.Vehicle(PlateKey));
        vehicle.EntityId.ShouldBe(TargetId);
        vehicle.Expandable.ShouldBeTrue();
        vehicle.Classification.ShouldBe<short?>(2);

        // Пересечения: по госномеру — от автомобиля к чужому делу; по адресу — к делу, которое субъект открывает.
        Edge(graph, vehicle.Key, RelationNodeKeys.ForeignCase(20)).Label.ShouldBe("пересечение: госномер");
        Edge(graph, home.Key, RelationNodeKeys.Case(21)).Kind.ShouldBe(RelationEdgeKind.Intersection);
        var openable = Node(graph, RelationNodeKeys.Case(21));
        openable.Expandable.ShouldBeTrue();
        openable.Classification.ShouldBe<short?>(1); // гриф и ведущий — из карточки доступного дела
        Edge(graph, center.Key, RelationNodeKeys.ForeignCase(23)).Label.ShouldBe("пересечение: лицо · подтверждено");

        // Отклонённое пересечение не рисуется (человек решил, что совпадение случайное).
        graph.Nodes.ShouldNotContain(n => n.Key.EndsWith(":22", StringComparison.Ordinal));

        // Соседи на глубине 1 не раскрывались: реквизиты брата не читались.
        await _requisites.DidNotReceive().GetAsync(BrotherId, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>());
        graph.Truncated.ShouldBeFalse();
    }

    [Fact(DisplayName = "ТБ-084: чужое дело — номер, вид, статус и ведущий; без идентификатора, грифа, ссылки и раскрытия")]
    public async Task Foreign_case_shows_only_intersection_fields()
    {
        var graph = (await Handle(new GetRelationGraphQuery(RelationNodeKind.Person, TargetId, Depth: 1))).Data.ShouldNotBeNull();

        var foreign = Node(graph, RelationNodeKeys.ForeignCase(20));
        foreign.Kind.ShouldBe(RelationNodeKind.ForeignCase);
        foreign.EntityId.ShouldBeNull();
        foreign.Classification.ShouldBeNull();
        foreign.Expandable.ShouldBeFalse();
        foreign.Title.ShouldBe("Дело № 20/26");
        foreign.Subtitle.ShouldBe($"{CaseKind.ObjectTask.Label()} · {CaseStatus.InProgress.Label()}");
        foreign.ResponsibleUserId.ShouldBe(77);

        // Фигуранты и карточка чужого дела не читаются вовсе.
        await _persons.DidNotReceive().ListByCaseAsync(20, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>());
        await _cases.DidNotReceive().GetAsync(20, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "ТО-мат-11: дело в центре на глубину 2 — один узел автомобиля на нормализованный госномер у двух фигурантов")]
    public async Task Case_center_merges_shared_vehicle()
    {
        var graph = (await Handle(new GetRelationGraphQuery(RelationNodeKind.Case, CaseId, Depth: 2))).Data.ShouldNotBeNull();

        graph.Nodes[0].Key.ShouldBe(RelationNodeKeys.Case(CaseId));
        graph.Nodes.Where(n => n.Kind == RelationNodeKind.Person && n.Level == 1).Select(n => n.EntityId)
            .ShouldBe(new int?[] { TargetId, BrotherId, OtherId }, ignoreOrder: true);
        graph.Nodes.Where(n => n.Kind == RelationNodeKind.Person && n.Level == 1).ShouldAllBe(n => n.Expanded);

        var vehicle = graph.Nodes.Where(n => n.Kind == RelationNodeKind.Vehicle).ShouldHaveSingleItem();
        graph.Edges.Where(e => e.ToKey == vehicle.Key && e.Kind == RelationEdgeKind.Requisite).Select(e => e.FromKey)
            .ShouldBe([RelationNodeKeys.Person(TargetId), RelationNodeKeys.Person(BrotherId)], ignoreOrder: true);

        // Связь из того же дела — одним ребром, хотя её видно с обеих сторон.
        graph.Edges.Count(e => e.Kind == RelationEdgeKind.PersonLink).ShouldBe(1);
    }

    [Fact(DisplayName = "ADR-0045 п. 4: соседи не помещаются в 300 узлов — узел остаётся свёрнутым, граф помечен урезанным")]
    public async Task Node_cap_keeps_node_collapsed()
    {
        var vehicles = Enumerable.Range(1, GetRelationGraphQuery.MaxNodes + 1)
            .Select(i => new PersonVehicleRow(100 + i, OtherId, $"01KG{i:000}XYZ", null, null, null, null))
            .ToList();
        _requisites.GetAsync(OtherId, Access, Arg.Any<CancellationToken>()).Returns(new PersonRequisites([], vehicles, []));

        var graph = (await Handle(new GetRelationGraphQuery(RelationNodeKind.Case, CaseId, Depth: 2))).Data.ShouldNotBeNull();

        graph.Truncated.ShouldBeTrue();
        Node(graph, RelationNodeKeys.Person(OtherId)).Expanded.ShouldBeFalse();
        graph.Nodes.Count.ShouldBeLessThanOrEqualTo(GetRelationGraphQuery.MaxNodes);
        graph.Nodes[0].Expanded.ShouldBeTrue(); // центр раскрывается всегда
    }

    [Fact(DisplayName = "ADR-0045 п. 10: автомобиль раскрывается в доступных фигурантов с тем же номером; в журнале — фигурант, не номер")]
    public async Task Vehicle_expands_to_accessible_owners()
    {
        _persons.ListByPlateAsync(PlateKey, Access, Arg.Any<CancellationToken>()).Returns([_target, _brother, _stranger]);
        var query = new GetRelationGraphQuery(RelationNodeKind.Vehicle, TargetId, Depth: 1, RelationNodeKeys.Vehicle(PlateKey));

        var graph = (await Handle(query)).Data.ShouldNotBeNull();

        graph.Nodes[0].Key.ShouldBe(RelationNodeKeys.Vehicle(PlateKey));
        graph.Nodes[0].Title.ShouldBe(Plate);
        graph.Nodes.Skip(1).Select(n => n.Key).ShouldBe(
            [RelationNodeKeys.Person(TargetId), RelationNodeKeys.Person(BrotherId), RelationNodeKeys.Person(StrangerId)], ignoreOrder: true);
        graph.Edges.ShouldAllBe(e => e.ToKey == RelationNodeKeys.Vehicle(PlateKey) && e.Kind == RelationEdgeKind.Requisite);
        Node(graph, RelationNodeKeys.Person(StrangerId)).Subtitle.ShouldBe("Объект · дело № 5-2026");

        query.AuditSummary.ShouldBe("investigation:graph:vehicle:person:10:view;depth=1");
        query.AuditSummary.ShouldNotBeNull().ShouldNotContain(PlateKey);
    }

    [Fact(DisplayName = "ADR-0045 п. 10: номер, которого у названного фигуранта нет, — «не найден»; перебор номеров не проходит")]
    public async Task Vehicle_probe_without_owner_is_not_found()
    {
        var response = await Handle(new GetRelationGraphQuery(
            RelationNodeKind.Vehicle, BrotherId, Depth: 1, RelationNodeKeys.Vehicle("99KG999ZZZ")));

        response.StatusCode.ShouldBe(ResponseStatusCode.NotFound);
        await _persons.DidNotReceiveWithAnyArgs().ListByPlateAsync(default!, default!, default);
    }

    [Fact(DisplayName = "ADR-0045 п. 10: адрес раскрывается в доступных фигурантов — строкой адреса или местом жительства из анкеты")]
    public async Task Address_expands_to_residents()
    {
        _persons.ListByAddressAsync(HomeKey, Access, Arg.Any<CancellationToken>()).Returns(
            [new PersonAddressMatch(_target, AddressKind.Residence), new PersonAddressMatch(_stranger, null)]);

        var graph = (await Handle(new GetRelationGraphQuery(
            RelationNodeKind.Address, TargetId, Depth: 1, RelationNodeKeys.Address(HomeKey)))).Data.ShouldNotBeNull();

        Edge(graph, RelationNodeKeys.Person(TargetId), RelationNodeKeys.Address(HomeKey)).Label.ShouldBe("проживание");
        Edge(graph, RelationNodeKeys.Person(StrangerId), RelationNodeKeys.Address(HomeKey)).Label.ShouldBe("жительство (анкета)");
    }

    [Fact(DisplayName = "ADR-0045 п. 11: совместные появления — ребро «вместе на материалах: N» к другому фигуранту")]
    public async Task Co_appearance_links_persons()
    {
        _persons.ListCoAppearancesAsync(TargetId, Access, Arg.Any<CancellationToken>()).Returns([new CoAppearanceRow(_stranger, 3)]);

        var graph = (await Handle(new GetRelationGraphQuery(RelationNodeKind.Person, TargetId, Depth: 1))).Data.ShouldNotBeNull();

        var together = Edge(graph, RelationNodeKeys.Person(TargetId), RelationNodeKeys.Person(StrangerId));
        together.Kind.ShouldBe(RelationEdgeKind.CoAppearance);
        together.Label.ShouldBe("вместе на материалах: 3");
    }

    [Fact(DisplayName = "ADR-0045 п. 12: цепочка до фигуранта другого дела — через общий автомобиль, узлы по порядку шагов")]
    public async Task Path_goes_through_shared_vehicle()
    {
        _persons.ListByPlateAsync(PlateKey, Access, Arg.Any<CancellationToken>()).Returns([_target, _stranger]);
        var query = new FindRelationPathQuery(RelationNodeKind.Person, TargetId, StrangerId);

        var path = (await HandlePath(query)).Data.ShouldNotBeNull();

        path.Nodes.Select(n => n.Key).ShouldBe(
            [RelationNodeKeys.Person(TargetId), RelationNodeKeys.Vehicle(PlateKey), RelationNodeKeys.Person(StrangerId)]);
        path.Nodes.Select(n => n.Level).ShouldBe([0, 1, 2]);
        path.Nodes[2].ParentKey.ShouldBe(RelationNodeKeys.Vehicle(PlateKey));
        path.Edges.Count.ShouldBe(2);
        path.Edges.ShouldAllBe(e => e.Kind == RelationEdgeKind.Requisite);
        query.AuditSummary.ShouldBe("investigation:graph:path:person:10:person:30:view");
    }

    [Fact(DisplayName = "ADR-0045 п. 12: нет цепочки — «не найдена в пределах доступных сведений»; недоступная цель — «не найден»")]
    public async Task Path_not_found_and_inaccessible_target()
    {
        var none = await HandlePath(new FindRelationPathQuery(RelationNodeKind.Person, TargetId, LonerId));
        none.StatusCode.ShouldBe(ResponseStatusCode.NotFound);
        none.StatusMessage.ShouldBe(FindRelationPathQuery.NoPath);

        var hidden = await HandlePath(new FindRelationPathQuery(RelationNodeKind.Person, TargetId, 999));
        hidden.StatusCode.ShouldBe(ResponseStatusCode.NotFound);
        hidden.StatusMessage.ShouldBe(GetRelationGraphQuery.NotFound);
    }

    [Fact(DisplayName = "ADR-0045 п. 12: поиск фигуранта — подписи как у узлов графа; в журнал — без текста запроса (ТБ-032)")]
    public async Task Person_search_returns_graph_nodes()
    {
        _persons.SearchAsync("Иван", SearchRelationPersonsQuery.MaxResults, Access, Arg.Any<CancellationToken>()).Returns([_target]);
        var query = new SearchRelationPersonsQuery("Иван");

        var response = await new SearchRelationPersonsQuery.Handler(_persons, _requisites, _cases, _intersections, _events, _references, _access)
            .Handle(query, CancellationToken.None);

        var hit = response.Data.ShouldNotBeNull().ShouldHaveSingleItem();
        hit.EntityId.ShouldBe(TargetId);
        hit.Subtitle.ShouldBe("Объект · «Шторм» · дело № 12-2026");
        query.AuditAction.ShouldBe(AuditAction.Search);
        query.AuditSummary.ShouldNotBeNull().ShouldNotContain("Иван");
    }

    [Fact(DisplayName = "ADR-0045 п. 14: выгрузка — запись Export с грифом картинки; ответ — кто сформировал; без сотрудника — отказ")]
    public async Task Export_is_audited_with_picture_classification()
    {
        var command = new ExportRelationGraphCommand(RelationNodeKind.Person, TargetId, RelationGraphExportFormat.Png, 12, Classification: 2);
        command.AuditAction.ShouldBe(AuditAction.Export);
        command.AuditClassification.ShouldBe<short?>(2);
        command.AuditSummary.ShouldBe("investigation:graph:person:10:export;format=png;nodes=12");

        var subject = Substitute.For<ISubjectProvider>();
        var accounts = Substitute.For<IUserAccountStore>();
        accounts.ListAsync(Arg.Any<CancellationToken>()).Returns(
            [new UserAccountRow(42, "petrov", "Петров П.П.", IsActive: true, HasLocalPassword: true, MustChangePassword: false)]);
        var handler = new ExportRelationGraphCommand.Handler(subject, accounts);

        subject.GetCurrentUserIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        (await handler.Handle(command, CancellationToken.None)).StatusMessage.ShouldBe(ExportRelationGraphCommand.NoSubject);

        subject.GetCurrentUserIdAsync(Arg.Any<CancellationToken>()).Returns((int?)42);
        (await handler.Handle(command, CancellationToken.None)).Data.ShouldBe("Петров П.П.");
    }

    [Fact(DisplayName = "ТБ-032: сводка аудита графа — вид, идентификатор и глубина, без ФИО и реквизитов")]
    public void Audit_summary_has_ids_only()
    {
        new GetRelationGraphQuery(RelationNodeKind.Person, TargetId, 3).AuditSummary
            .ShouldBe("investigation:graph:person:10:view;depth=3");
        new GetRelationGraphQuery(RelationNodeKind.Case, CaseId).AuditSummary
            .ShouldBe("investigation:graph:case:3:view;depth=2");
    }

    [Fact(DisplayName = "ADR-0045 п. 3, 10: граф — от фигуранта, дела, а автомобиль и адрес — только с ключом своего вида; глубина 1–8")]
    public void Validator_accepts_expandable_centers_and_depth_range()
    {
        var validator = new GetRelationGraphValidator();

        validator.Validate(new GetRelationGraphQuery(RelationNodeKind.Person, 1, 8)).IsValid.ShouldBeTrue();
        validator.Validate(new GetRelationGraphQuery(RelationNodeKind.Case, 1, 1)).IsValid.ShouldBeTrue();
        validator.Validate(new GetRelationGraphQuery(RelationNodeKind.Vehicle, 1, 1, RelationNodeKeys.Vehicle("01KG"))).IsValid.ShouldBeTrue();
        validator.Validate(new GetRelationGraphQuery(RelationNodeKind.Address, 1, 1, RelationNodeKeys.Address("ул"))).IsValid.ShouldBeTrue();
        validator.Validate(new GetRelationGraphQuery(RelationNodeKind.ForeignCase, 1)).IsValid.ShouldBeFalse();
        validator.Validate(new GetRelationGraphQuery(RelationNodeKind.Vehicle, 1)).IsValid.ShouldBeFalse();
        validator.Validate(new GetRelationGraphQuery(RelationNodeKind.Vehicle, 1, 1, RelationNodeKeys.Address("ул"))).IsValid.ShouldBeFalse();
        validator.Validate(new GetRelationGraphQuery(RelationNodeKind.Vehicle, 1, 1, RelationNodeKeys.VehicleRow(5))).IsValid.ShouldBeFalse();
        validator.Validate(new GetRelationGraphQuery(RelationNodeKind.Person, 1, 1, RelationNodeKeys.Vehicle("01KG"))).IsValid.ShouldBeFalse();
        validator.Validate(new GetRelationGraphQuery(RelationNodeKind.Person, 1, 0)).IsValid.ShouldBeFalse();
        validator.Validate(new GetRelationGraphQuery(RelationNodeKind.Person, 1, 9)).IsValid.ShouldBeFalse();
        validator.Validate(new GetRelationGraphQuery(RelationNodeKind.Person, 0)).IsValid.ShouldBeFalse();
    }

    [Fact(DisplayName = "ADR-0055: телефон — узел на нормализованный номер у фигуранта, пересечение по номеру — от узла номера; раскрытие — фигуранты с этим номером")]
    public async Task Phone_node_intersection_and_expansion()
    {
        const string Number = "0555 123 456";
        var phoneKey = RequisiteNormalizer.Phone(Number)!;
        _requisites.GetAsync(TargetId, Access, Arg.Any<CancellationToken>()).Returns(new PersonRequisites(
            [], [],
            [
                new PersonPhoneRow(1, TargetId, PhoneKind.Mobile, Number, null),
                new PersonPhoneRow(2, TargetId, PhoneKind.Work, "+996 555 123 456", null), // тот же номер иначе — один узел
            ]));
        _intersections.FindForPersonAsync(TargetId, Access, Arg.Any<CancellationToken>())
            .Returns([Match(IntersectionKind.Phone, phoneKey, otherCaseId: 24, canOpen: false)]);
        _persons.ListByPhoneAsync(phoneKey, Access, Arg.Any<CancellationToken>()).Returns([_target, _brother]);

        var graph = (await Handle(new GetRelationGraphQuery(RelationNodeKind.Person, TargetId, Depth: 1))).Data.ShouldNotBeNull();

        var phone = Node(graph, RelationNodeKeys.Phone(phoneKey));
        phone.Kind.ShouldBe(RelationNodeKind.Phone);
        phone.EntityId.ShouldBe(TargetId);
        phone.Title.ShouldBe(Number);
        phone.Expandable.ShouldBeTrue();
        Edge(graph, RelationNodeKeys.Person(TargetId), phone.Key).Label.ShouldBe("телефон · мобильный");
        Edge(graph, phone.Key, RelationNodeKeys.ForeignCase(24)).Label.ShouldBe("пересечение: телефон");

        // Раскрытие узла номера — через фигуранта, у которого он есть (ADR-0045 п. 10).
        var expanded = (await Handle(new GetRelationGraphQuery(RelationNodeKind.Phone, TargetId, Depth: 1, phone.Key))).Data.ShouldNotBeNull();
        expanded.Nodes.ShouldContain(n => n.Key == RelationNodeKeys.Person(BrotherId));
        (await Handle(new GetRelationGraphQuery(RelationNodeKind.Phone, BrotherId, Depth: 1, phone.Key))).StatusCode
            .ShouldBe(ResponseStatusCode.NotFound); // у брата такого номера нет — ключ ничего не разрешает

        new GetRelationGraphQuery(RelationNodeKind.Phone, TargetId, 1, phone.Key).AuditSummary
            .ShouldBe($"investigation:graph:phone:person:{TargetId}:view;depth=1"); // без номера (ТБ-032)
        new GetRelationGraphValidator().Validate(new GetRelationGraphQuery(RelationNodeKind.Phone, TargetId, 1, null)).IsValid.ShouldBeFalse();
    }

    [Fact(DisplayName = "ADR-0056: событие — узел (название, вид, дата, гриф); ребро участия — роль и «предположительно»; раскрытие — доступные участники и чужое дело без ФИО; «неверно» не рисуется")]
    public async Task Event_node_and_participants()
    {
        var linkEvent = new LinkEventRow(900, LinkEventKind.Meeting, "Встреча у рынка", new DateOnly(2026, 10, 5), "обсуждали", 2, 5, 42,
            new DateTime(2026, 10, 6, 0, 0, 0, DateTimeKind.Utc),
            [
                Participant(1, _target, LinkLevel.Probable, "водитель"),
                Participant(2, _stranger, LinkLevel.Confirmed),
                new LinkEventParticipantRow(3, null, null, null, 77, "77/26", CaseKind.ObjectTask, CaseStatus.InProgress, 99, false,
                    null, LinkLevel.Confirmed, false, null, null, null, 1),
                Participant(4, _loner, LinkLevel.Wrong),
            ]);
        _events.ListForPersonAsync(TargetId, Access, Arg.Any<CancellationToken>()).Returns([linkEvent]);
        _events.ListForPersonAsync(LonerId, Access, Arg.Any<CancellationToken>()).Returns([linkEvent]);
        _events.GetAsync(900, Access, Arg.Any<CancellationToken>()).Returns(linkEvent);

        var graph = (await Handle(new GetRelationGraphQuery(RelationNodeKind.Person, TargetId, Depth: 1))).Data.ShouldNotBeNull();

        var node = Node(graph, RelationNodeKeys.Event(900));
        node.Kind.ShouldBe(RelationNodeKind.Event);
        node.EntityId.ShouldBe(900);
        node.Title.ShouldBe("Встреча у рынка");
        node.Subtitle.ShouldBe("Встреча · 05.10.2026");
        node.Classification.ShouldBe<short?>(2);
        node.Expandable.ShouldBeTrue();
        Edge(graph, RelationNodeKeys.Person(TargetId), node.Key).Label.ShouldBe("водитель · предположительно");
        Edge(graph, RelationNodeKeys.Person(TargetId), node.Key).Kind.ShouldBe(RelationEdgeKind.EventLink);

        // Событие в центре (ФТ-5.2.5): все видимые участники; чужой — делом без ФИО (ТБ-084); «неверно» — нет.
        var query = new GetRelationGraphQuery(RelationNodeKind.Event, 900, Depth: 1);
        var around = (await Handle(query)).Data.ShouldNotBeNull();
        around.Nodes[0].Key.ShouldBe(RelationNodeKeys.Event(900));
        Edge(around, RelationNodeKeys.Person(StrangerId), RelationNodeKeys.Event(900)).Label.ShouldBe("участник");
        var foreign = Node(around, RelationNodeKeys.ForeignCase(77));
        foreign.Title.ShouldBe("Дело № 77/26");
        foreign.EntityId.ShouldBeNull();
        foreign.ResponsibleUserId.ShouldBe(99);
        Edge(around, foreign.Key, RelationNodeKeys.Event(900)).Label.ShouldBe("участник события");
        around.Nodes.ShouldNotContain(n => n.Key == RelationNodeKeys.Person(LonerId));
        query.AuditSummary.ShouldBe("investigation:graph:event:900:view;depth=1");

        // У того, чьё участие «неверно», события на графе нет.
        var lonerGraph = (await Handle(new GetRelationGraphQuery(RelationNodeKind.Person, LonerId, Depth: 1))).Data.ShouldNotBeNull();
        lonerGraph.Nodes.ShouldNotContain(n => n.Kind == RelationNodeKind.Event);

        // Недоступное событие — «не найден», как фигурант или дело.
        (await Handle(new GetRelationGraphQuery(RelationNodeKind.Event, 901, Depth: 1))).StatusCode.ShouldBe(ResponseStatusCode.NotFound);
    }

    [Fact(DisplayName = "ПМИ 4: «как связан с…» — цепочка 2-го порядка через события: Объект 2 → Событие 2 → Объект 3 → Событие 3 → Объект 4")]
    public async Task Path_goes_through_events()
    {
        var second = Lone(102, 62);
        var third = Lone(103, 63);
        var fourth = Lone(104, 64);
        LinkEvents(Event(2, second, third), Event(3, third, fourth));

        var path = (await HandlePath(new FindRelationPathQuery(RelationNodeKind.Person, second.Id, fourth.Id))).Data.ShouldNotBeNull();

        path.Nodes.Select(n => n.Key).ShouldBe(
        [
            RelationNodeKeys.Person(102), RelationNodeKeys.Event(2), RelationNodeKeys.Person(103), RelationNodeKeys.Event(3),
            RelationNodeKeys.Person(104),
        ]);
        path.Edges.ShouldAllBe(e => e.Kind == RelationEdgeKind.EventLink);
    }

    [Fact(DisplayName = "ПМИ 5: кольцо Объект 1 → 2 → 3 → 4 → 1 через события — граф на наибольшую глубину строится без зацикливания, каждый узел один раз")]
    public async Task Ring_of_events_does_not_loop()
    {
        var people = Enumerable.Range(1, 4).Select(i => Lone(110 + i, 70 + i)).ToArray();
        LinkEvents(
            Event(11, people[0], people[1]), Event(12, people[1], people[2]), Event(13, people[2], people[3]), Event(14, people[3], people[0]));

        var graph = (await Handle(new GetRelationGraphQuery(RelationNodeKind.Person, people[0].Id, GetRelationGraphQuery.MaxDepth)))
            .Data.ShouldNotBeNull();

        graph.Nodes.Select(n => n.Key).ShouldBeUnique();
        graph.Nodes.Count(n => n.Kind == RelationNodeKind.Event).ShouldBe(4);
        graph.Nodes.Count(n => n.Kind == RelationNodeKind.Person).ShouldBe(4);
        graph.Edges.Count(e => e.Kind == RelationEdgeKind.EventLink).ShouldBe(8); // кольцо замкнуто: у каждого события два ребра
        graph.Truncated.ShouldBeFalse();
    }

    // Фигурант в отдельном деле без связей и реквизитов — звено цепочки только через события.
    private PersonRow Lone(int id, int caseId)
    {
        var row = Person(id, $"Объект {id}", PersonRole.Target, caseId);
        _persons.GetAsync(id, Access, Arg.Any<CancellationToken>()).Returns(row);
        _persons.ListCoAppearancesAsync(id, Access, Arg.Any<CancellationToken>()).Returns([]);
        _persons.ListByCaseAsync(caseId, Access, Arg.Any<CancellationToken>()).Returns([row]);
        _intersections.FindForPersonAsync(id, Access, Arg.Any<CancellationToken>()).Returns([]);
        _requisites.GetAsync(id, Access, Arg.Any<CancellationToken>()).Returns(new PersonRequisites([], [], []));
        _cases.GetAsync(caseId, Access, Arg.Any<CancellationToken>()).Returns(Case(caseId, $"{caseId}/26"));
        return row;
    }

    // События под заглушки хранилища: по событию и по каждому участнику — его события.
    private void LinkEvents(params LinkEventRow[] list)
    {
        foreach (var linkEvent in list)
        {
            _events.GetAsync(linkEvent.Id, Access, Arg.Any<CancellationToken>()).Returns(linkEvent);
        }

        foreach (var personId in list.SelectMany(e => e.Participants).Select(p => p.PersonId!.Value).Distinct())
        {
            IReadOnlyList<LinkEventRow> own = list.Where(e => e.Participants.Any(p => p.PersonId == personId)).ToList();
            _events.ListForPersonAsync(personId, Access, Arg.Any<CancellationToken>()).Returns(own);
        }
    }

    private static LinkEventRow Event(int id, params PersonRow[] people) =>
        new(id, LinkEventKind.Contact, $"Событие {id}", null, null, 0, 5, 42, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
            people.Select((person, index) => Participant((id * 10) + index, person, LinkLevel.Confirmed)).ToList());

    private static LinkEventParticipantRow Participant(int id, PersonRow person, LinkLevel level, string? role = null) =>
        new(id, person.Id, person.DisplayName, null, person.CaseId, $"{person.CaseId}/26", CaseKind.ObjectTask, CaseStatus.InProgress, 42,
            IsAccessible: true, role, level, InSearch: true, null, null, null, person.Classification);

    private async Task<ResponseDto<RelationGraph>> Handle(GetRelationGraphQuery query) =>
        await new GetRelationGraphQuery.Handler(_persons, _requisites, _cases, _intersections, _events, _references, _access)
            .Handle(query, CancellationToken.None);

    private async Task<ResponseDto<RelationGraph>> HandlePath(FindRelationPathQuery query) =>
        await new FindRelationPathQuery.Handler(_persons, _requisites, _cases, _intersections, _events, _references, _access)
            .Handle(query, CancellationToken.None);

    private static RelationNode Node(RelationGraph graph, string key) =>
        graph.Nodes.Where(n => n.Key == key).ShouldHaveSingleItem();

    private static RelationEdge Edge(RelationGraph graph, string from, string to) =>
        graph.Edges.Where(e => e.FromKey == from && e.ToKey == to).ShouldHaveSingleItem();

    private static PersonRow Person(
        int id, string name, PersonRole role, int caseId, PersonQuestionnaire? questionnaire = null, int? linkedTo = null,
        int? linkType = null, short classification = 0) =>
        new(id, caseId, name, IsUnidentified: false, UnidentifiedNumber: null, RoleInCase: null, Notes: null, classification,
            DivisionId: 5, ReferencePhotoCount: 0, AppearanceCount: 0, role, questionnaire, linkedTo, linkType);

    private static CaseDetails Case(int id, string number, short classification = 0) => new(
        id, number, "Задание", CaseKind.ObjectTask, CaseStatus.InProgress, new DateOnly(2026, 9, 1), InvestigatorUserId: 42,
        DivisionId: 5, classification, Basis: null, ClosedAt: null, CreatedAt: new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc),
        Media: [], Authorizations: []);

    private static IntersectionRow Match(
        IntersectionKind kind, string key, int otherCaseId, bool canOpen, IntersectionDecision? decision = null) =>
        new(kind, key, "своё", "чужое", otherCaseId, $"{otherCaseId}/26", CaseKind.ObjectTask, CaseStatus.InProgress,
            ResponsibleUserId: 77, canOpen, decision, DecidedByUserId: null, DecidedAt: null);
}
