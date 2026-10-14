using System;
using System.Collections.Generic;
using Blazor.Diagrams.Core.Geometry;

namespace ISC.AI.Profile.Investigation.UI;

/// <summary>
/// Раскладка графа связей (ADR-0045 п. 8) — как в АСИО-10: центр в середине холста, его соседи — полным кругом,
/// соседи остальных узлов — веером наружу от центра графа; радиус кольца уменьшается с удалением от центра.
/// В отличие от АСИО-10 радиус растёт, когда соседей много, — карточки на дуге не наползают друг на друга.
/// </summary>
public static class RelationGraphLayout
{
    /// <summary>Точка центра графа на холсте.</summary>
    public static Point Center { get; } = new(560, 360);

    /// <summary>Радиус соседей при раскрытии узла щелчком.</summary>
    public const double ClickRadius = 200;

    /// <summary>Ширина веера (радианы) у узла, не являющегося центром.</summary>
    public const double FanSpread = Math.PI * 1.15;

    /// <summary>Наименьшее расстояние между соседями по дуге, px.</summary>
    public const double MinArcSpacing = 120;

    /// <summary>Радиус кольца соседей узла кольца <paramref name="anchorLevel"/>: 260, 225, 190, … но не меньше 150.</summary>
    public static double RingRadius(int anchorLevel) => Math.Max(150, 260 - (anchorLevel * 35));

    /// <summary>Позиции <paramref name="count"/> новых соседей узла <paramref name="anchor"/>.</summary>
    /// <param name="anchor">Положение раскрываемого узла.</param>
    /// <param name="anchorIsCenter">Раскрывается центр графа — соседи полным кругом.</param>
    /// <param name="count">Число новых соседей.</param>
    /// <param name="radius">Желаемый радиус; увеличивается, если соседи не помещаются на дуге.</param>
    public static IReadOnlyList<Point> Place(Point anchor, bool anchorIsCenter, int count, double radius)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        if (count <= 0)
        {
            return [];
        }

        var spread = anchorIsCenter ? 2 * Math.PI : FanSpread;
        var baseAngle = anchorIsCenter ? 0 : Math.Atan2(anchor.Y - Center.Y, anchor.X - Center.X);
        var effectiveRadius = Math.Max(radius, count * MinArcSpacing / spread);

        var result = new List<Point>(count);
        for (var i = 0; i < count; i++)
        {
            var angle = anchorIsCenter
                ? baseAngle + (2 * Math.PI * i / count)
                : baseAngle - (spread / 2) + (spread * (count == 1 ? 0.5 : (double)i / (count - 1)));
            result.Add(new Point(anchor.X + (effectiveRadius * Math.Cos(angle)), anchor.Y + (effectiveRadius * Math.Sin(angle))));
        }

        return result;
    }
}
