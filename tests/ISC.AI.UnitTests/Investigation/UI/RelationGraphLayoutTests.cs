using System;
using System.Linq;
using Blazor.Diagrams.Core.Geometry;
using ISC.AI.Profile.Investigation.UI;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Investigation.UI;

/// <summary>
/// Раскладка графа связей (ADR-0045 п. 8) — как в АСИО-10: соседи центра полным кругом, соседи остальных узлов — веером
/// наружу от центра; при большом числе соседей радиус растёт, чтобы карточки не наползали.
/// </summary>
public sealed class RelationGraphLayoutTests
{
    private static readonly Point Center = RelationGraphLayout.Center;

    [Fact(DisplayName = "ADR-0045: соседи центра — полным кругом на радиусе кольца")]
    public void Center_neighbors_form_full_circle()
    {
        var points = RelationGraphLayout.Place(Center, anchorIsCenter: true, count: 4, radius: 260);

        points.Count.ShouldBe(4);
        points.ShouldAllBe(p => Math.Abs(Distance(p, Center) - 260) < 0.001);
        points[0].X.ShouldBe(Center.X + 260, 0.001);
        points[1].Y.ShouldBe(Center.Y + 260, 0.001);
        points[2].X.ShouldBe(Center.X - 260, 0.001);
    }

    [Fact(DisplayName = "ADR-0045: соседи остального узла — веером наружу, дальше от центра графа, чем сам узел")]
    public void Other_neighbors_fan_outwards()
    {
        var anchor = new Point(Center.X + 260, Center.Y);

        var points = RelationGraphLayout.Place(anchor, anchorIsCenter: false, count: 3, radius: 200);

        points.ShouldAllBe(p => Math.Abs(Distance(p, anchor) - 200) < 0.001);
        points[1].X.ShouldBe(anchor.X + 200, 0.001); // средний — прямо по направлению от центра
        points[1].Y.ShouldBe(anchor.Y, 0.001);
        points.Average(p => Distance(p, Center)).ShouldBeGreaterThan(Distance(anchor, Center));
    }

    [Fact(DisplayName = "ADR-0045: много соседей — радиус растёт, расстояние по дуге не меньше 120 px")]
    public void Radius_grows_with_neighbor_count()
    {
        var points = RelationGraphLayout.Place(Center, anchorIsCenter: true, count: 40, radius: 260);

        var radius = Distance(points[0], Center);
        radius.ShouldBeGreaterThan(260);
        (2 * Math.PI * radius / 40).ShouldBeGreaterThanOrEqualTo(RelationGraphLayout.MinArcSpacing - 0.001);
    }

    [Fact(DisplayName = "ADR-0045: радиус кольца уменьшается с удалением от центра, но не меньше 150")]
    public void Ring_radius_shrinks_with_level()
    {
        RelationGraphLayout.RingRadius(0).ShouldBe(260);
        RelationGraphLayout.RingRadius(1).ShouldBe(225);
        RelationGraphLayout.RingRadius(8).ShouldBe(150);
        RelationGraphLayout.Place(Center, anchorIsCenter: false, count: 0, radius: 200).ShouldBeEmpty();
    }

    private static double Distance(Point a, Point b) => Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));
}
