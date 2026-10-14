using System;
using System.Collections.Generic;
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

    [Fact(DisplayName = "ADR-0045 п. 9: силовая раскладка — карточки не перекрываются, центр закреплён, раскладка повторяется один в один")]
    public void Arrange_removes_overlaps_and_is_deterministic()
    {
        // Звезда из 12 узлов, все начально в одной точке (худший случай), плюс цепочка из двух.
        var boxes = Enumerable.Range(0, 12).Select(i => new LayoutBox($"n{i}", 100, 100, 190, 70))
            .Prepend(new LayoutBox("c", Center.X, Center.Y, 190, 70))
            .Append(new LayoutBox("a", 0, 0, 190, 70))
            .Append(new LayoutBox("b", 0, 0, 190, 70))
            .ToList();
        var edges = Enumerable.Range(0, 12).Select(i => ("c", $"n{i}")).Append(("n0", "a")).Append(("a", "b")).ToList();
        var pinned = new HashSet<string>(StringComparer.Ordinal) { "c" };

        var first = RelationGraphLayout.Arrange(boxes, edges, pinned);
        var second = RelationGraphLayout.Arrange(boxes, edges, pinned);

        first["c"].X.ShouldBe(Center.X);
        first["c"].Y.ShouldBe(Center.Y);
        first.Keys.ShouldAllBe(key => first[key].X == second[key].X && first[key].Y == second[key].Y);

        var placed = boxes.Select(b => b with { X = first[b.Key].X, Y = first[b.Key].Y }).ToList();
        for (var i = 0; i < placed.Count; i++)
        {
            for (var j = i + 1; j < placed.Count; j++)
            {
                Overlaps(placed[i], placed[j]).ShouldBeFalse($"{placed[i].Key} и {placed[j].Key} перекрываются");
            }
        }
    }

    [Fact(DisplayName = "ADR-0045 п. 9: после раскрытия щелчком двигаются только новые узлы — закреплённые остаются на месте")]
    public void Arrange_keeps_fixed_boxes()
    {
        var boxes = new List<LayoutBox>
        {
            new("old1", 0, 0, 190, 70), new("old2", 400, 0, 190, 70), new("new1", 0, 0, 190, 70), new("new2", 0, 0, 190, 70),
        };
        var fixedKeys = new HashSet<string>(StringComparer.Ordinal) { "old1", "old2" };

        var result = RelationGraphLayout.Arrange(boxes, [("old1", "new1"), ("old1", "new2")], fixedKeys, iterations: 150);

        result["old1"].X.ShouldBe(0);
        result["old2"].X.ShouldBe(400);
        Overlaps(boxes[0] with { X = result["old1"].X, Y = result["old1"].Y }, boxes[2] with { X = result["new1"].X, Y = result["new1"].Y })
            .ShouldBeFalse();
    }

    [Fact(DisplayName = "ADR-0045 п. 9: «Вписать» — граф в окне с полями, крупнее 1:1 не увеличивает")]
    public void Fit_scales_down_but_not_up()
    {
        var wide = new List<LayoutBox> { new("a", 0, 0, 190, 70), new("b", 2810, 1430, 190, 70) }; // 3000 × 1500
        var (zoom, panX, panY) = RelationGraphLayout.Fit(wide, 1240, 840, margin: 20);
        zoom.ShouldBe(0.4, 0.0001);
        panX.ShouldBe(20, 0.0001);
        panY.ShouldBe(20 + ((800 - 600) / 2.0), 0.0001);

        var small = new List<LayoutBox> { new("a", 100, 100, 190, 70) };
        RelationGraphLayout.Fit(small, 1240, 840).Zoom.ShouldBe(1);
        RelationGraphLayout.Fit([], 1240, 840).ShouldBe((1, 0, 0));
    }

    private static bool Overlaps(LayoutBox a, LayoutBox b) =>
        a.X < b.X + b.Width && b.X < a.X + a.Width && a.Y < b.Y + b.Height && b.Y < a.Y + a.Height;

    private static double Distance(Point a, Point b) => Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));
}
