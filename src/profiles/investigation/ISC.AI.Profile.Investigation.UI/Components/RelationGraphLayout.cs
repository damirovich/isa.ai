using System;
using System.Collections.Generic;
using System.Linq;
using Blazor.Diagrams.Core.Geometry;

namespace ISC.AI.Profile.Investigation.UI;

/// <summary>Карточка узла для раскладки: ключ, левый верхний угол и размер.</summary>
/// <param name="Key">Ключ узла.</param>
/// <param name="X">Левый край.</param>
/// <param name="Y">Верхний край.</param>
/// <param name="Width">Ширина.</param>
/// <param name="Height">Высота.</param>
public sealed record LayoutBox(string Key, double X, double Y, double Width, double Height);

/// <summary>
/// Раскладка графа связей (ADR-0045 п. 8–9) — силовая (<see cref="Arrange"/>): связанные узлы притягиваются, все
/// отталкиваются, затем перекрытия карточек убираются. Начальные положения — веер, как в АСИО-10 (<see cref="Place"/>):
/// центр в середине холста, его соседи — полным кругом, соседи остальных узлов — веером наружу от центра. Отдельным
/// режимом веер не показывается (заказчик 14.10.2026: «веером не нужен»). Случайности нет: одинаковый граф ложится
/// одинаково.
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

    /// <summary>Желаемая длина ребра силовой раскладки (между центрами карточек), px: место для карточек и подписи.</summary>
    public const double IdealEdgeLength = 300;

    /// <summary>Зазор между карточками после раскладки, px.</summary>
    public const double Gap = 24;

    /// <summary>Шагов силовой раскладки по умолчанию.</summary>
    public const int DefaultIterations = 300;

    /// <summary>Радиус кольца соседей узла кольца <paramref name="anchorLevel"/>: 260, 225, 190, … но не меньше 150.</summary>
    public static double RingRadius(int anchorLevel) => Math.Max(150, 260 - (anchorLevel * 35));

    /// <summary>Начальные позиции <paramref name="count"/> новых соседей узла <paramref name="anchor"/> (веер).</summary>
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

    /// <summary>
    /// Силовая раскладка (ADR-0045 п. 9) по Фрухтерману — Рейнгольду: все карточки отталкиваются
    /// (k²/d), концы рёбер притягиваются (d²/k), слабое притяжение к центру холста держит вместе несвязные части;
    /// шаг ограничен «температурой», которая остывает к концу. Затем перекрытия карточек убираются
    /// (<see cref="RemoveOverlaps"/>). Закреплённые узлы (<paramref name="fixedKeys"/>) не двигаются: центр графа, а при
    /// раскрытии щелчком — всё, что уже было на холсте.
    /// </summary>
    /// <param name="boxes">Карточки в текущих положениях (начальные положения — например, веер).</param>
    /// <param name="edges">Рёбра: пары ключей.</param>
    /// <param name="fixedKeys">Ключи закреплённых карточек.</param>
    /// <param name="iterations">Шагов раскладки.</param>
    /// <returns>Новые левые верхние углы всех карточек.</returns>
    public static IReadOnlyDictionary<string, Point> Arrange(
        IReadOnlyList<LayoutBox> boxes,
        IReadOnlyList<(string From, string To)> edges,
        IReadOnlySet<string> fixedKeys,
        int iterations = DefaultIterations)
    {
        ArgumentNullException.ThrowIfNull(boxes);
        ArgumentNullException.ThrowIfNull(edges);
        ArgumentNullException.ThrowIfNull(fixedKeys);

        var n = boxes.Count;
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var cx = new double[n];
        var cy = new double[n];
        var movable = new bool[n];
        for (var i = 0; i < n; i++)
        {
            index[boxes[i].Key] = i;
            cx[i] = boxes[i].X + (boxes[i].Width / 2);
            cy[i] = boxes[i].Y + (boxes[i].Height / 2);
            movable[i] = !fixedKeys.Contains(boxes[i].Key);
        }

        var links = edges
            .Where(e => index.ContainsKey(e.From) && index.ContainsKey(e.To) && !string.Equals(e.From, e.To, StringComparison.Ordinal))
            .Select(e => (A: index[e.From], B: index[e.To]))
            .Distinct()
            .ToList();

        const double k = IdealEdgeLength;
        const double startTemperature = 90;
        const double gravity = 0.02;
        var dx = new double[n];
        var dy = new double[n];

        for (var step = 0; step < iterations && movable.Any(m => m); step++)
        {
            Array.Clear(dx);
            Array.Clear(dy);

            // Отталкивание всех пар.
            for (var i = 0; i < n; i++)
            {
                for (var j = i + 1; j < n; j++)
                {
                    var ddx = cx[i] - cx[j];
                    var ddy = cy[i] - cy[j];
                    var d2 = (ddx * ddx) + (ddy * ddy);
                    if (d2 < 1)
                    {
                        // Совпавшие точки: детерминированный сдвиг по номерам, без случайности.
                        ddx = ((i % 7) - 3) + 0.5;
                        ddy = ((j % 5) - 2) + 0.5;
                        d2 = (ddx * ddx) + (ddy * ddy);
                    }

                    var d = Math.Sqrt(d2);
                    var force = k * k / d;
                    var fx = ddx / d * force;
                    var fy = ddy / d * force;
                    dx[i] += fx;
                    dy[i] += fy;
                    dx[j] -= fx;
                    dy[j] -= fy;
                }
            }

            // Притяжение концов рёбер.
            foreach (var (a, b) in links)
            {
                var ddx = cx[a] - cx[b];
                var ddy = cy[a] - cy[b];
                var d = Math.Max(Math.Sqrt((ddx * ddx) + (ddy * ddy)), 1);
                var force = d * d / k;
                var fx = ddx / d * force;
                var fy = ddy / d * force;
                dx[a] -= fx;
                dy[a] -= fy;
                dx[b] += fx;
                dy[b] += fy;
            }

            var temperature = (startTemperature * (1 - ((double)step / iterations))) + 1;
            for (var i = 0; i < n; i++)
            {
                if (!movable[i])
                {
                    continue;
                }

                // Слабое притяжение к центру холста: несвязные части не разлетаются.
                dx[i] += (Center.X - cx[i]) * gravity * k / 10;
                dy[i] += (Center.Y - cy[i]) * gravity * k / 10;

                var length = Math.Sqrt((dx[i] * dx[i]) + (dy[i] * dy[i]));
                if (length > 0)
                {
                    var move = Math.Min(length, temperature);
                    cx[i] += dx[i] / length * move;
                    cy[i] += dy[i] / length * move;
                }
            }
        }

        var arranged = boxes
            .Select((box, i) => box with { X = cx[i] - (box.Width / 2), Y = cy[i] - (box.Height / 2) })
            .ToList();
        return RemoveOverlaps(arranged, fixedKeys);
    }

    /// <summary>
    /// Убирает перекрытия карточек (с зазором <see cref="Gap"/>): пара раздвигается по оси наименьшего перекрытия,
    /// поровну, а если одна закреплена — двигается другая; две закреплённые не трогаются. Повторяется, пока перекрытия
    /// есть (не больше 200 проходов).
    /// </summary>
    /// <param name="boxes">Карточки.</param>
    /// <param name="fixedKeys">Ключи закреплённых карточек.</param>
    /// <returns>Левые верхние углы всех карточек.</returns>
    public static IReadOnlyDictionary<string, Point> RemoveOverlaps(IReadOnlyList<LayoutBox> boxes, IReadOnlySet<string> fixedKeys)
    {
        ArgumentNullException.ThrowIfNull(boxes);
        ArgumentNullException.ThrowIfNull(fixedKeys);

        var n = boxes.Count;
        var x = boxes.Select(b => b.X).ToArray();
        var y = boxes.Select(b => b.Y).ToArray();
        var movable = boxes.Select(b => !fixedKeys.Contains(b.Key)).ToArray();

        for (var pass = 0; pass < 200; pass++)
        {
            var moved = false;
            for (var i = 0; i < n; i++)
            {
                for (var j = i + 1; j < n; j++)
                {
                    if (!movable[i] && !movable[j])
                    {
                        continue;
                    }

                    var ci = (X: x[i] + (boxes[i].Width / 2), Y: y[i] + (boxes[i].Height / 2));
                    var cj = (X: x[j] + (boxes[j].Width / 2), Y: y[j] + (boxes[j].Height / 2));
                    var overlapX = ((boxes[i].Width + boxes[j].Width) / 2) + Gap - Math.Abs(ci.X - cj.X);
                    var overlapY = ((boxes[i].Height + boxes[j].Height) / 2) + Gap - Math.Abs(ci.Y - cj.Y);
                    if (overlapX <= 0 || overlapY <= 0)
                    {
                        continue;
                    }

                    // Раздвигаем по оси меньшего перекрытия; направление — от j к i (при совпадении — по номерам).
                    var shareI = movable[i] && movable[j] ? 0.5 : movable[i] ? 1.0 : 0.0;
                    var shareJ = 1.0 - shareI;
                    if (overlapX <= overlapY)
                    {
                        var sign = ci.X > cj.X || (ci.X == cj.X && i > j) ? 1.0 : -1.0;
                        x[i] += sign * overlapX * shareI;
                        x[j] -= sign * overlapX * shareJ;
                    }
                    else
                    {
                        var sign = ci.Y > cj.Y || (ci.Y == cj.Y && i > j) ? 1.0 : -1.0;
                        y[i] += sign * overlapY * shareI;
                        y[j] -= sign * overlapY * shareJ;
                    }

                    moved = true;
                }
            }

            if (!moved)
            {
                break;
            }
        }

        var result = new Dictionary<string, Point>(StringComparer.Ordinal);
        for (var i = 0; i < n; i++)
        {
            result[boxes[i].Key] = new Point(x[i], y[i]);
        }

        return result;
    }

    /// <summary>
    /// Масштаб и сдвиг холста, чтобы все карточки поместились в окно с полями (кнопка «Вписать», ADR-0045 п. 9);
    /// крупнее 1:1 не увеличивает, мельче 0,2 не уменьшает.
    /// </summary>
    /// <param name="boxes">Видимые карточки.</param>
    /// <param name="containerWidth">Ширина окна холста.</param>
    /// <param name="containerHeight">Высота окна холста.</param>
    /// <param name="margin">Поле по краям, px.</param>
    /// <returns>Масштаб и сдвиг (экранная точка = точка холста × масштаб + сдвиг); пустой граф — 1 и без сдвига.</returns>
    public static (double Zoom, double PanX, double PanY) Fit(
        IReadOnlyList<LayoutBox> boxes, double containerWidth, double containerHeight, double margin = 40)
    {
        ArgumentNullException.ThrowIfNull(boxes);
        if (boxes.Count == 0 || containerWidth <= 0 || containerHeight <= 0)
        {
            return (1, 0, 0);
        }

        var minX = boxes.Min(b => b.X);
        var minY = boxes.Min(b => b.Y);
        var width = Math.Max(boxes.Max(b => b.X + b.Width) - minX, 1);
        var height = Math.Max(boxes.Max(b => b.Y + b.Height) - minY, 1);
        var availableWidth = Math.Max(containerWidth - (2 * margin), 1);
        var availableHeight = Math.Max(containerHeight - (2 * margin), 1);

        var zoom = Math.Clamp(Math.Min(availableWidth / width, availableHeight / height), 0.2, 1);
        var panX = margin + ((availableWidth - (width * zoom)) / 2) - (minX * zoom);
        var panY = margin + ((availableHeight - (height * zoom)) / 2) - (minY * zoom);
        return (zoom, panX, panY);
    }
}
