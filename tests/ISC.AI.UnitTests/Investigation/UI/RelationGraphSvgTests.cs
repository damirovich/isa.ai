using System;
using System.Linq;
using System.Xml.Linq;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Application.Features.RelationGraph;
using ISC.AI.Profile.Investigation.UI;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Investigation.UI;

/// <summary>
/// Картинка графа для выгрузки (ADR-0045 п. 14): корректный SVG без внешних ресурсов, гриф вверху и внизу, текст
/// экранирован, линии со стрелками и цветами по виду ребра, ребро к невидимой карточке не рисуется.
/// </summary>
public sealed class RelationGraphSvgTests
{
    private static readonly SvgNode Center = new("person:1", RelationNodeKind.Person, "Иванов <Иван> & Ко", ["Объект · дело № 1"],
        0, 0, 190, 70, IsCenter: true);

    private static readonly SvgNode Foreign = new("foreign-case:9", RelationNodeKind.ForeignCase, "Дело № 9/26",
        ["Задание по объекту · В производстве", "ведёт Петров", "чужое дело"], 400, 0, 190, 90, IsCenter: false);

    [Fact(DisplayName = "ADR-0045 п. 14: SVG разбирается как XML; гриф — вверху и внизу; текст экранирован; заголовок и подпись на месте")]
    public void Svg_is_valid_marked_and_escaped()
    {
        var marking = ClassificationLevels.Marking(ClassificationLevels.Secret);

        var svg = RelationGraphSvg.Render(
            [Center, Foreign], [new SvgEdge(Center.Key, Foreign.Key, RelationEdgeKind.Intersection, "пересечение: лицо")],
            "Граф связей: Иванов", "Сформировал: Петров · 14.10.2026 12:00", marking);

        var document = XDocument.Parse(svg); // экранирование «<», «&» — иначе разбор упал бы
        var texts = document.Descendants().Where(e => e.Name.LocalName == "text").Select(e => e.Value).ToList();
        texts.Count(t => t == marking).ShouldBe(2);
        texts.ShouldContain("Граф связей: Иванов");
        texts.ShouldContain("Сформировал: Петров · 14.10.2026 12:00");
        texts.ShouldContain(t => t.Contains("Иванов <Иван>", StringComparison.Ordinal));
        svg.ShouldNotContain("href", Case.Insensitive); // никаких внешних ресурсов — PNG рисуется без «загрязнения» холста
    }

    [Fact(DisplayName = "ADR-0045 п. 14: пересечение — оранжевая линия со стрелкой, чужое дело — пунктир; ребро к невидимой карточке не рисуется")]
    public void Edges_follow_kind_and_visibility()
    {
        var svg = RelationGraphSvg.Render(
            [Center, Foreign],
            [
                new SvgEdge(Center.Key, Foreign.Key, RelationEdgeKind.Intersection, "пересечение: лицо"),
                new SvgEdge(Center.Key, "vehicle:HIDDEN", RelationEdgeKind.Requisite, "автотранспорт"),
            ],
            "t", "f", "НЕСЕКРЕТНО");

        var document = XDocument.Parse(svg);
        var line = document.Descendants().Where(e => e.Name.LocalName == "line").ShouldHaveSingleItem();
        line.Attribute("stroke")!.Value.ShouldBe(RelationGraphSvg.IntersectionColor);
        line.Attribute("marker-end")!.Value.ShouldBe("url(#arrow-x)");
        document.Descendants().Where(e => e.Name.LocalName == "rect")
            .ShouldContain(e => e.Attribute("stroke-dasharray") != null);
        svg.ShouldNotContain("автотранспорт");
    }
}
