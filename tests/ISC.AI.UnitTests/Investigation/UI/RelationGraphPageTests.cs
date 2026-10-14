using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Blazor.Diagrams.Core.Geometry;
using Bunit;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Profiles;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Application.Features.Directory;
using ISC.AI.Profile.Investigation.Application.Features.RelationGraph;
using ISC.AI.Profile.Investigation.UI;
using Mediator;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;
using Shouldly;
using Xunit;

// Подмены NSubstitute для IMediator.Send: ValueTask здесь не исполняется, а задаёт ответ или проверяет вызов
// (CA2012 к этому неприменим).
#pragma warning disable CA2012

namespace ISC.AI.UnitTests.Investigation.UI;

/// <summary>
/// Страница графа связей (ТФ-ПЕР-10, ADR-0045): граф строится одним запросом с центром из маршрута; чужое дело — без
/// ссылки «открыть»; щелчок по свёрнутому узлу («?») — тот же запрос с глубиной 1 от узла, повторный щелчок
/// сворачивает ветку. Запросы Mediator подменены, JS — в свободном режиме bUnit.
/// </summary>
public sealed class RelationGraphPageTests : BunitContext, IAsyncLifetime
{
    private const int PersonId = 10;
    private readonly IMediator _mediator = Substitute.For<IMediator>();

    public RelationGraphPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;

        // Холст библиотеки диаграмм спрашивает у браузера свои размеры; в bUnit браузера нет — отвечаем сами.
        JSInterop.Setup<Rectangle>("ZBlazorDiagrams.getBoundingClientRect", _ => true).SetResult(new Rectangle(0, 0, 1200, 800));
        Services.AddMudServices();
        Services.AddSingleton(_mediator);
        var profile = Substitute.For<IProfile>();
        profile.DisplayName.Returns("Следствие");
        Services.AddSingleton(profile);
        SetRendererInfo(new RendererInfo("Server", true));

        _mediator.Send(Arg.Any<ListUserDirectoryQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<IReadOnlyList<UserAccountRow>>>(ResponseDto<IReadOnlyList<UserAccountRow>>.Ok(
                [new UserAccountRow(77, "petrov", "Петров П.П.", IsActive: true, HasLocalPassword: true, MustChangePassword: false)])));

        var center = Node(RelationNodeKeys.Person(PersonId), RelationNodeKind.Person, PersonId, "Иванов Иван", 0, null, expanded: true);
        var brother = Node(RelationNodeKeys.Person(11), RelationNodeKind.Person, 11, "Иванов Пётр", 1, center.Key) with
        {
            Subtitle = "Связь · дело № 12-2026",
        };
        var foreign = Node(RelationNodeKeys.ForeignCase(20), RelationNodeKind.ForeignCase, null, "Дело № 20/26", 1, center.Key) with
        {
            ResponsibleUserId = 77,
        };
        Respond(q => q.CenterId == PersonId, new RelationGraph(
            [center, brother, foreign],
            [
                new RelationEdge(brother.Key, center.Key, RelationEdgeKind.PersonLink, "брат"),
                new RelationEdge(center.Key, foreign.Key, RelationEdgeKind.Intersection, "пересечение: лицо"),
            ],
            Truncated: false));

        // Раскрытие брата: его автомобиль.
        var expandedBrother = brother with { Level = 0, ParentKey = null, Expanded = true };
        var car = Node(RelationNodeKeys.Vehicle("01KG123ABC"), RelationNodeKind.Vehicle, null, "01 KG 123 ABC", 1, brother.Key);
        Respond(q => q.CenterId == 11, new RelationGraph(
            [expandedBrother, car],
            [new RelationEdge(brother.Key, car.Key, RelationEdgeKind.Requisite, "автотранспорт")],
            Truncated: false));
    }

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

    Task IAsyncLifetime.DisposeAsync() => base.DisposeAsync().AsTask();

    [Fact(DisplayName = "ТФ-ПЕР-10: граф фигуранта — запрос с центром из маршрута и глубиной 2; чужое дело без «открыть», с ведущим")]
    public void Builds_graph_for_route_center()
    {
        var cut = RenderPage();

        cut.WaitForAssertion(() => cut.FindAll(".rgraph-node").Count.ShouldBe(3));
        _mediator.Received(1).Send(
            Arg.Is<GetRelationGraphQuery>(q => q.CenterKind == RelationNodeKind.Person && q.CenterId == PersonId && q.Depth == 2),
            Arg.Any<CancellationToken>());

        var foreign = NodeCard(cut, "Дело № 20/26");
        foreign.ClassList.ShouldContain("rgraph-node--foreign");
        foreign.TextContent.ShouldContain("ведёт Петров П.П.");
        foreign.QuerySelector(".rgraph-node__open").ShouldBeNull();
        foreign.QuerySelector(".rgraph-node__badge--q").ShouldBeNull(); // не раскрывается

        NodeCard(cut, "Иванов Иван").QuerySelector(".rgraph-node__open")!.GetAttribute("href").ShouldBe("/persons/10");
        NodeCard(cut, "Иванов Пётр").QuerySelector(".rgraph-node__badge--q").ShouldNotBeNull();
    }

    [Fact(DisplayName = "ADR-0045 п. 5: подпись ребра — одна строка, полный текст в подсказке; у узла в подсказке весь текст и что делает щелчок")]
    public void Full_text_is_shown_on_hover()
    {
        var cut = RenderPage();
        cut.WaitForAssertion(() => cut.FindAll(".rgraph-edge-label").Count.ShouldBe(2));

        // Своя подпись вместо подписи библиотеки: полный текст — в title; пересечение выделено цветом.
        var intersection = EdgeLabel(cut, "пересечение: лицо");
        intersection.GetAttribute("title").ShouldBe("пересечение: лицо");
        intersection.ClassList.ShouldContain("rgraph-edge-label--intersection");
        EdgeLabel(cut, "брат").ClassList.ShouldNotContain("rgraph-edge-label--intersection");
        cut.FindAll(".default-link-label").ShouldBeEmpty();

        // Подсказка узла: заголовок, вторая строка, ведущий — построчно; затем подсказка про щелчок.
        NodeCard(cut, "Иванов Пётр").GetAttribute("title")
            .ShouldBe("Иванов Пётр\nСвязь · дело № 12-2026\nЩелчок — показать связи");
        var foreign = NodeCard(cut, "Дело № 20/26").GetAttribute("title").ShouldNotBeNull();
        foreign.ShouldStartWith("Дело № 20/26\nведёт Петров П.П.\n");
        foreign.ShouldContain("Чужое дело");
    }

    [Fact(DisplayName = "ADR-0045 п. 5: щелчок по «?» раскрывает узел запросом глубины 1, повторный — сворачивает ветку")]
    public void Click_expands_and_collapses_node()
    {
        var cut = RenderPage();
        cut.WaitForAssertion(() => cut.FindAll(".rgraph-node").Count.ShouldBe(3));

        NodeCard(cut, "Иванов Пётр").Click();
        cut.WaitForAssertion(() => cut.FindAll(".rgraph-node").Count.ShouldBe(4));
        _mediator.Received(1).Send(
            Arg.Is<GetRelationGraphQuery>(q => q.CenterKind == RelationNodeKind.Person && q.CenterId == 11 && q.Depth == 1),
            Arg.Any<CancellationToken>());
        NodeCard(cut, "Иванов Пётр").QuerySelector(".rgraph-node__badge--q").ShouldBeNull();

        NodeCard(cut, "Иванов Пётр").Click();
        cut.WaitForAssertion(() => cut.FindAll(".rgraph-node").Count.ShouldBe(3));
        cut.Markup.ShouldNotContain("01 KG 123 ABC");
        NodeCard(cut, "Иванов Пётр").QuerySelector(".rgraph-node__badge--q").ShouldNotBeNull();
    }

    private IRenderedComponent<RelationGraphPage> RenderPage()
    {
        Render<MudPopoverProvider>();
        return Render<RelationGraphPage>(p => p.Add(x => x.PersonId, PersonId));
    }

    private static AngleSharp.Dom.IElement NodeCard(IRenderedComponent<RelationGraphPage> cut, string title) =>
        cut.FindAll(".rgraph-node").Single(e => e.QuerySelector(".rgraph-node__title")!.TextContent.Trim() == title);

    private static AngleSharp.Dom.IElement EdgeLabel(IRenderedComponent<RelationGraphPage> cut, string text) =>
        cut.FindAll(".rgraph-edge-label").Single(e => e.TextContent.Trim() == text);

    private void Respond(Func<GetRelationGraphQuery, bool> match, RelationGraph graph) =>
        _mediator.Send(Arg.Is<GetRelationGraphQuery>(q => match(q)), Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask<ResponseDto<RelationGraph>>(ResponseDto<RelationGraph>.Ok(graph)));

    private static RelationNode Node(
        string key, RelationNodeKind kind, int? id, string title, int level, string? parent, bool expanded = false) =>
        new(key, kind, id, title, Subtitle: null, level, parent, expanded);
}
