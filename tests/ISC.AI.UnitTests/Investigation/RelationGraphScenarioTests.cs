using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Application;
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
/// отклонённые пересечения не рисуются; предохранитель числа узлов; в журнал — только идентификаторы (ТБ-032).
/// </summary>
public sealed class RelationGraphScenarioTests
{
    private const int CaseId = 3;
    private const int TargetId = 10;
    private const int BrotherId = 11;
    private const int OtherId = 12;
    private const int BrotherLinkType = 7;
    private const string Residence = "г. Бишкек, ул. Ленина 1";

    private static readonly AccessContext Access = new("42", MaxClassification: 2, AllowedDivisions: [5]);

    private readonly IPersonStore _persons = Substitute.For<IPersonStore>();
    private readonly IPersonRequisiteStore _requisites = Substitute.For<IPersonRequisiteStore>();
    private readonly ICaseStore _cases = Substitute.For<ICaseStore>();
    private readonly IIntersectionStore _intersections = Substitute.For<IIntersectionStore>();
    private readonly IReferenceStore _references = Substitute.For<IReferenceStore>();
    private readonly IAccessContextProvider _access = Substitute.For<IAccessContextProvider>();

    public RelationGraphScenarioTests()
    {
        _access.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(Access);
        _references.ListAsync(ReferenceKind.LinkType, Arg.Any<CancellationToken>())
            .Returns([new ReferenceItemRow(BrotherLinkType, ReferenceKind.LinkType, "брат", null, 1, IsActive: true)]);

        var target = Person(TargetId, "Иванов Иван", PersonRole.Target,
            questionnaire: new PersonQuestionnaire(Residence: Residence, Alias: "Шторм"));
        var brother = Person(BrotherId, "Иванов Пётр", PersonRole.Link, linkedTo: TargetId, linkType: BrotherLinkType);
        var other = Person(OtherId, "Сидоров", PersonRole.Other);
        _persons.GetAsync(TargetId, Access, Arg.Any<CancellationToken>()).Returns(target);
        _persons.ListByCaseAsync(CaseId, Access, Arg.Any<CancellationToken>()).Returns([target, brother, other]);
        _cases.GetAsync(CaseId, Access, Arg.Any<CancellationToken>()).Returns(Case(CaseId, "12-2026"));

        // У объекта и брата — один и тот же автомобиль, записанный по-разному; адрес проживания объекта совпадает с анкетой.
        _requisites.GetAsync(TargetId, Access, Arg.Any<CancellationToken>()).Returns(new PersonRequisites(
            [
                new PersonAddressRow(1, TargetId, AddressKind.Residence, Residence, null),
                new PersonAddressRow(2, TargetId, AddressKind.Work, "ул. Токтогула 5", null),
            ],
            [new PersonVehicleRow(1, TargetId, "01 KG 123 ABC", "Toyota", "Camry", "белый", null)]));
        _requisites.GetAsync(BrotherId, Access, Arg.Any<CancellationToken>()).Returns(new PersonRequisites(
            [], [new PersonVehicleRow(2, BrotherId, "01kg123abc", "Toyota", null, null, null)]));
        _requisites.GetAsync(OtherId, Access, Arg.Any<CancellationToken>()).Returns(new PersonRequisites([], []));

        _intersections.FindForPersonAsync(TargetId, Access, Arg.Any<CancellationToken>()).Returns(
        [
            Match(IntersectionKind.Vehicle, RequisiteNormalizer.Plate("01 KG 123 ABC")!, otherCaseId: 20, canOpen: false),
            Match(IntersectionKind.Address, RequisiteNormalizer.Address(Residence)!, otherCaseId: 21, canOpen: true),
            Match(IntersectionKind.PersonName, "иванов иван", otherCaseId: 22, canOpen: false, IntersectionDecision.Rejected),
            Match(IntersectionKind.Face, "face", otherCaseId: 23, canOpen: false, IntersectionDecision.Confirmed),
        ]);
        _intersections.FindForPersonAsync(BrotherId, Access, Arg.Any<CancellationToken>()).Returns([]);
        _intersections.FindForPersonAsync(OtherId, Access, Arg.Any<CancellationToken>()).Returns([]);
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

        Node(graph, RelationNodeKeys.Case(CaseId)).Title.ShouldBe("Дело № 12-2026");
        var brother = Node(graph, RelationNodeKeys.Person(BrotherId));
        brother.Expanded.ShouldBeFalse();
        brother.ParentKey.ShouldBe(center.Key);
        Edge(graph, brother.Key, center.Key).Label.ShouldBe("брат");
        graph.Nodes.ShouldNotContain(n => n.Key == RelationNodeKeys.Person(OtherId)); // не связь объекта

        // Место жительства из анкеты и строка адреса проживания — один узел и одно ребро.
        var home = Node(graph, RelationNodeKeys.Address(RequisiteNormalizer.Address(Residence)!));
        graph.Edges.Count(e => e.ToKey == home.Key && e.Kind == RelationEdgeKind.Requisite).ShouldBe(1);
        graph.Edges.ShouldNotContain(e => e.Label == "жительство (анкета)");

        // Пересечения: по госномеру — от автомобиля к чужому делу; по адресу — к делу, которое субъект открывает.
        var vehicle = Node(graph, RelationNodeKeys.Vehicle(RequisiteNormalizer.Plate("01 KG 123 ABC")!));
        Edge(graph, vehicle.Key, RelationNodeKeys.ForeignCase(20)).Label.ShouldBe("пересечение: госномер");
        Edge(graph, home.Key, RelationNodeKeys.Case(21)).Kind.ShouldBe(RelationEdgeKind.Intersection);
        Node(graph, RelationNodeKeys.Case(21)).Expandable.ShouldBeTrue();
        Edge(graph, center.Key, RelationNodeKeys.ForeignCase(23)).Label.ShouldBe("пересечение: лицо · подтверждено");

        // Отклонённое пересечение не рисуется (человек решил, что совпадение случайное).
        graph.Nodes.ShouldNotContain(n => n.Key.EndsWith(":22", StringComparison.Ordinal));

        // Соседи на глубине 1 не раскрывались: реквизиты брата не читались.
        await _requisites.DidNotReceive().GetAsync(BrotherId, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>());
        graph.Truncated.ShouldBeFalse();
    }

    [Fact(DisplayName = "ТБ-084: чужое дело — номер, вид, статус и ведущий; без идентификатора, ссылки и раскрытия")]
    public async Task Foreign_case_shows_only_intersection_fields()
    {
        var graph = (await Handle(new GetRelationGraphQuery(RelationNodeKind.Person, TargetId, Depth: 1))).Data.ShouldNotBeNull();

        var foreign = Node(graph, RelationNodeKeys.ForeignCase(20));
        foreign.Kind.ShouldBe(RelationNodeKind.ForeignCase);
        foreign.EntityId.ShouldBeNull();
        foreign.Expandable.ShouldBeFalse();
        foreign.Title.ShouldBe("Дело № 20/26");
        foreign.Subtitle.ShouldBe($"{CaseKind.ObjectTask.Label()} · {CaseStatus.InProgress.Label()}");
        foreign.ResponsibleUserId.ShouldBe(77);

        // Фигуранты чужого дела не читаются вовсе.
        await _persons.DidNotReceive().ListByCaseAsync(20, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "ТО-мат-11: дело в центре на глубину 2 — один узел автомобиля на нормализованный госномер у двух фигурантов")]
    public async Task Case_center_merges_shared_vehicle()
    {
        var graph = (await Handle(new GetRelationGraphQuery(RelationNodeKind.Case, CaseId, Depth: 2))).Data.ShouldNotBeNull();

        graph.Nodes[0].Key.ShouldBe(RelationNodeKeys.Case(CaseId));
        graph.Nodes.Where(n => n.Kind == RelationNodeKind.Person).Select(n => n.EntityId)
            .ShouldBe(new int?[] { TargetId, BrotherId, OtherId }, ignoreOrder: true);
        graph.Nodes.Where(n => n.Kind == RelationNodeKind.Person).ShouldAllBe(n => n.Expanded);

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
        _requisites.GetAsync(OtherId, Access, Arg.Any<CancellationToken>()).Returns(new PersonRequisites([], vehicles));

        var graph = (await Handle(new GetRelationGraphQuery(RelationNodeKind.Case, CaseId, Depth: 2))).Data.ShouldNotBeNull();

        graph.Truncated.ShouldBeTrue();
        Node(graph, RelationNodeKeys.Person(OtherId)).Expanded.ShouldBeFalse();
        graph.Nodes.Count.ShouldBeLessThanOrEqualTo(GetRelationGraphQuery.MaxNodes);
        graph.Nodes[0].Expanded.ShouldBeTrue(); // центр раскрывается всегда
    }

    [Fact(DisplayName = "ТБ-032: сводка аудита графа — вид, идентификатор и глубина, без ФИО и реквизитов")]
    public void Audit_summary_has_ids_only()
    {
        new GetRelationGraphQuery(RelationNodeKind.Person, TargetId, 3).AuditSummary
            .ShouldBe("investigation:graph:person:10:view;depth=3");
        new GetRelationGraphQuery(RelationNodeKind.Case, CaseId).AuditSummary
            .ShouldBe("investigation:graph:case:3:view;depth=2");
    }

    [Fact(DisplayName = "ADR-0045 п. 3: граф строится только от фигуранта или дела, глубина 1–8")]
    public void Validator_accepts_only_person_or_case_and_depth_range()
    {
        var validator = new GetRelationGraphValidator();

        validator.Validate(new GetRelationGraphQuery(RelationNodeKind.Person, 1, 8)).IsValid.ShouldBeTrue();
        validator.Validate(new GetRelationGraphQuery(RelationNodeKind.Case, 1, 1)).IsValid.ShouldBeTrue();
        validator.Validate(new GetRelationGraphQuery(RelationNodeKind.ForeignCase, 1)).IsValid.ShouldBeFalse();
        validator.Validate(new GetRelationGraphQuery(RelationNodeKind.Vehicle, 1)).IsValid.ShouldBeFalse();
        validator.Validate(new GetRelationGraphQuery(RelationNodeKind.Person, 1, 0)).IsValid.ShouldBeFalse();
        validator.Validate(new GetRelationGraphQuery(RelationNodeKind.Person, 1, 9)).IsValid.ShouldBeFalse();
        validator.Validate(new GetRelationGraphQuery(RelationNodeKind.Person, 0)).IsValid.ShouldBeFalse();
    }

    private async Task<ResponseDto<RelationGraph>> Handle(GetRelationGraphQuery query) =>
        await new GetRelationGraphQuery.Handler(_persons, _requisites, _cases, _intersections, _references, _access)
            .Handle(query, CancellationToken.None);

    private static RelationNode Node(RelationGraph graph, string key) =>
        graph.Nodes.Where(n => n.Key == key).ShouldHaveSingleItem();

    private static RelationEdge Edge(RelationGraph graph, string from, string to) =>
        graph.Edges.Where(e => e.FromKey == from && e.ToKey == to).ShouldHaveSingleItem();

    private static PersonRow Person(
        int id, string name, PersonRole role, PersonQuestionnaire? questionnaire = null, int? linkedTo = null, int? linkType = null) =>
        new(id, CaseId, name, IsUnidentified: false, UnidentifiedNumber: null, RoleInCase: null, Notes: null, Classification: 0,
            DivisionId: 5, ReferencePhotoCount: 0, AppearanceCount: 0, role, questionnaire, linkedTo, linkType);

    private static CaseDetails Case(int id, string number) => new(
        id, number, "Задание", CaseKind.ObjectTask, CaseStatus.InProgress, new DateOnly(2026, 9, 1), InvestigatorUserId: 42,
        DivisionId: 5, Classification: 0, Basis: null, ClosedAt: null, CreatedAt: new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc),
        Media: [], Authorizations: []);

    private static IntersectionRow Match(
        IntersectionKind kind, string key, int otherCaseId, bool canOpen, IntersectionDecision? decision = null) =>
        new(kind, key, "своё", "чужое", otherCaseId, $"{otherCaseId}/26", CaseKind.ObjectTask, CaseStatus.InProgress,
            ResponsibleUserId: 77, canOpen, decision, DecidedByUserId: null, DecidedAt: null);
}
