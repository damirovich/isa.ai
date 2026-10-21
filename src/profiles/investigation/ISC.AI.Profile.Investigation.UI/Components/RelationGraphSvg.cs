using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using ISC.AI.Profile.Investigation.Application.Features.RelationGraph;

namespace ISC.AI.Profile.Investigation.UI;

/// <summary>Карточка узла на выгружаемой картинке: положение и размер — как на холсте.</summary>
/// <param name="Key">Ключ узла.</param>
/// <param name="Kind">Вид узла.</param>
/// <param name="Title">Заголовок.</param>
/// <param name="Lines">Дополнительные строки (роль и дело, ведущий и т. п.).</param>
/// <param name="X">Левый край на холсте.</param>
/// <param name="Y">Верхний край на холсте.</param>
/// <param name="Width">Ширина.</param>
/// <param name="Height">Высота.</param>
/// <param name="IsCenter">Центр графа.</param>
public sealed record SvgNode(
    string Key, RelationNodeKind Kind, string Title, IReadOnlyList<string> Lines, double X, double Y, double Width, double Height, bool IsCenter);

/// <summary>Ребро на выгружаемой картинке.</summary>
/// <param name="FromKey">Начало.</param>
/// <param name="ToKey">Конец.</param>
/// <param name="Kind">Вид ребра.</param>
/// <param name="Label">Подпись.</param>
public sealed record SvgEdge(string FromKey, string ToKey, RelationEdgeKind Kind, string Label);

/// <summary>
/// Картинка графа связей для выгрузки (ADR-0045 п. 14): SVG из модели графа — без сторонних библиотек и без внешних
/// ресурсов (шрифты системные), поэтому браузер переводит его в PNG через canvas без «загрязнения». Сверху и снизу —
/// гриф картинки (ТБ-024), сверху — заголовок, снизу — кто и когда сформировал.
/// </summary>
public static class RelationGraphSvg
{
    /// <summary>Цвет обычной линии (как на холсте).</summary>
    public const string EdgeColor = "#9e9e9e";

    /// <summary>Цвет линии пересечения.</summary>
    public const string IntersectionColor = "#ef6c00";

    /// <summary>Цвет линии совместного появления.</summary>
    public const string CoAppearanceColor = "#2e7d32";

    private const double Margin = 40;
    private const double HeaderHeight = 64;
    private const double FooterHeight = 44;
    private const int MaxLabelChars = 40;
    private const int TitleCharsPerLine = 24;
    private const int LineChars = 30;
    private const string Font = "Segoe UI, Arial, sans-serif";

    /// <summary>Собрать SVG.</summary>
    /// <param name="nodes">Видимые карточки.</param>
    /// <param name="edges">Видимые рёбра (концы — среди карточек).</param>
    /// <param name="title">Заголовок картинки.</param>
    /// <param name="footer">Нижняя строка: кто и когда сформировал.</param>
    /// <param name="marking">Гриф картинки — строка разметки, вверху и внизу.</param>
    public static string Render(IReadOnlyList<SvgNode> nodes, IReadOnlyList<SvgEdge> edges, string title, string footer, string marking)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(edges);

        var minX = nodes.Count == 0 ? 0 : nodes.Min(n => n.X);
        var minY = nodes.Count == 0 ? 0 : nodes.Min(n => n.Y);
        var maxX = nodes.Count == 0 ? 400 : nodes.Max(n => n.X + n.Width);
        var maxY = nodes.Count == 0 ? 200 : nodes.Max(n => n.Y + n.Height);
        var width = Math.Max(maxX - minX + (2 * Margin), 600);
        var height = maxY - minY + (2 * Margin) + HeaderHeight + FooterHeight;
        var offsetX = Margin - minX;
        var offsetY = Margin + HeaderHeight - minY;

        var byKey = nodes.ToDictionary(n => n.Key, StringComparer.Ordinal);
        var svg = new StringBuilder();
        svg.Append(Invariant($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{width:0}\" height=\"{height:0}\" viewBox=\"0 0 {width:0} {height:0}\" font-family=\"{Font}\">"));
        svg.Append("<defs>");
        foreach (var (id, color) in new[] { ("arrow", EdgeColor), ("arrow-x", IntersectionColor), ("arrow-c", CoAppearanceColor) })
        {
            svg.Append(Invariant($"<marker id=\"{id}\" viewBox=\"0 0 10 10\" refX=\"10\" refY=\"5\" markerWidth=\"8\" markerHeight=\"8\" orient=\"auto-start-reverse\"><path d=\"M0,0 L10,5 L0,10 z\" fill=\"{color}\"/></marker>"));
        }

        svg.Append("</defs>");
        svg.Append(Invariant($"<rect x=\"0\" y=\"0\" width=\"{width:0}\" height=\"{height:0}\" fill=\"#ffffff\"/>"));

        // Шапка: гриф и заголовок.
        svg.Append(Invariant($"<text x=\"{width / 2:0.#}\" y=\"24\" text-anchor=\"middle\" font-size=\"14\" font-weight=\"700\" fill=\"#b71c1c\">{Escape(marking)}</text>"));
        svg.Append(Invariant($"<text x=\"{Margin:0.#}\" y=\"52\" font-size=\"16\" font-weight=\"600\" fill=\"#212121\">{Escape(title)}</text>"));

        // Рёбра — под карточками: от края до края карточек, с подписью посередине.
        foreach (var edge in edges)
        {
            if (!byKey.TryGetValue(edge.FromKey, out var from) || !byKey.TryGetValue(edge.ToKey, out var to))
            {
                continue;
            }

            var (x1, y1) = Center(from, offsetX, offsetY);
            var (x2, y2) = Center(to, offsetX, offsetY);
            (x1, y1) = Clip(x1, y1, x2, y2, from);
            (x2, y2) = Clip(x2, y2, x1, y1, to);
            var color = Color(edge.Kind);
            var marker = edge.Kind switch
            {
                RelationEdgeKind.PersonLink => " marker-end=\"url(#arrow)\"",
                RelationEdgeKind.Intersection => " marker-end=\"url(#arrow-x)\"",
                _ => string.Empty,
            };
            svg.Append(Invariant($"<line x1=\"{x1:0.#}\" y1=\"{y1:0.#}\" x2=\"{x2:0.#}\" y2=\"{y2:0.#}\" stroke=\"{color}\" stroke-width=\"1.5\"{marker}/>"));

            var label = Shorten(edge.Label, MaxLabelChars);
            var labelWidth = (label.Length * 6.2) + 12;
            var mx = (x1 + x2) / 2;
            var my = (y1 + y2) / 2;
            var fill = edge.Kind switch
            {
                RelationEdgeKind.Intersection => IntersectionColor,
                RelationEdgeKind.CoAppearance => CoAppearanceColor,
                _ => "#5c8fd6",
            };
            svg.Append(Invariant($"<rect x=\"{mx - (labelWidth / 2):0.#}\" y=\"{my - 9:0.#}\" width=\"{labelWidth:0.#}\" height=\"18\" rx=\"4\" fill=\"{fill}\"/>"));
            svg.Append(Invariant($"<text x=\"{mx:0.#}\" y=\"{my + 4:0.#}\" text-anchor=\"middle\" font-size=\"11\" fill=\"#ffffff\">{Escape(label)}</text>"));
        }

        // Карточки.
        foreach (var node in nodes)
        {
            var x = node.X + offsetX;
            var y = node.Y + offsetY;
            var stroke = node.IsCenter ? "#c62828" : "#bdbdbd";
            var strokeWidth = node.IsCenter ? 2 : 1;
            var dash = node.Kind == RelationNodeKind.ForeignCase ? " stroke-dasharray=\"5,3\"" : string.Empty;
            svg.Append(Invariant($"<rect x=\"{x:0.#}\" y=\"{y:0.#}\" width=\"{node.Width:0.#}\" height=\"{node.Height:0.#}\" rx=\"8\" fill=\"#ffffff\" stroke=\"{stroke}\" stroke-width=\"{strokeWidth}\"{dash}/>"));
            svg.Append(Invariant($"<rect x=\"{x:0.#}\" y=\"{y + 4:0.#}\" width=\"4\" height=\"{Math.Max(node.Height - 8, 0):0.#}\" fill=\"{KindColor(node.Kind)}\"/>"));

            var lineY = y + 18;
            foreach (var titleLine in Wrap(node.Title, TitleCharsPerLine, maxLines: 2))
            {
                svg.Append(Invariant($"<text x=\"{x + 12:0.#}\" y=\"{lineY:0.#}\" font-size=\"13\" font-weight=\"600\" fill=\"#212121\">{Escape(titleLine)}</text>"));
                lineY += 16;
            }

            foreach (var line in node.Lines)
            {
                svg.Append(Invariant($"<text x=\"{x + 12:0.#}\" y=\"{lineY:0.#}\" font-size=\"11\" fill=\"#616161\">{Escape(Shorten(line, LineChars))}</text>"));
                lineY += 14;
            }
        }

        // Подвал: кто и когда, гриф.
        svg.Append(Invariant($"<text x=\"{Margin:0.#}\" y=\"{height - 26:0.#}\" font-size=\"11\" fill=\"#616161\">{Escape(footer)}</text>"));
        svg.Append(Invariant($"<text x=\"{width / 2:0.#}\" y=\"{height - 8:0.#}\" text-anchor=\"middle\" font-size=\"14\" font-weight=\"700\" fill=\"#b71c1c\">{Escape(marking)}</text>"));
        svg.Append("</svg>");
        return svg.ToString();
    }

    /// <summary>Цвет линии по виду ребра (как на холсте).</summary>
    public static string Color(RelationEdgeKind kind) => kind switch
    {
        RelationEdgeKind.Intersection => IntersectionColor,
        RelationEdgeKind.CoAppearance => CoAppearanceColor,
        _ => EdgeColor,
    };

    /// <summary>Цвет полосы вида узла (как на холсте; значения, а не переменные палитры — картинка без темы).</summary>
    public static string KindColor(RelationNodeKind kind) => kind switch
    {
        RelationNodeKind.Person => "#594ae2",
        RelationNodeKind.Case => "#2196f3",
        RelationNodeKind.ForeignCase => "#ff9800",
        RelationNodeKind.Vehicle => "#ff4081",
        RelationNodeKind.Phone => "#1ec8a5",
        RelationNodeKind.Event => "#f44336",
        _ => "#00c853",
    };

    /// <summary>Экранирование текста для XML.</summary>
    public static string Escape(string? text) =>
        string.IsNullOrEmpty(text)
            ? string.Empty
            : text.Replace("&", "&amp;", StringComparison.Ordinal)
                .Replace("<", "&lt;", StringComparison.Ordinal)
                .Replace(">", "&gt;", StringComparison.Ordinal)
                .Replace("\"", "&quot;", StringComparison.Ordinal)
                .Replace("'", "&apos;", StringComparison.Ordinal);

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private static (double X, double Y) Center(SvgNode node, double offsetX, double offsetY) =>
        (node.X + offsetX + (node.Width / 2), node.Y + offsetY + (node.Height / 2));

    // Точка выхода отрезка (cx,cy)→(tx,ty) из прямоугольника карточки с центром (cx,cy).
    private static (double X, double Y) Clip(double cx, double cy, double tx, double ty, SvgNode node)
    {
        var dx = tx - cx;
        var dy = ty - cy;
        if (dx == 0 && dy == 0)
        {
            return (cx, cy);
        }

        var halfWidth = node.Width / 2;
        var halfHeight = node.Height / 2;
        var scale = Math.Min(
            dx == 0 ? double.MaxValue : halfWidth / Math.Abs(dx),
            dy == 0 ? double.MaxValue : halfHeight / Math.Abs(dy));
        scale = Math.Min(scale, 1);
        return (cx + (dx * scale), cy + (dy * scale));
    }

    private static string Shorten(string text, int max) =>
        text.Length <= max ? text : string.Concat(text.AsSpan(0, max - 1), "…");

    // Перенос по словам в не больше maxLines строк; остаток — с многоточием.
    private static List<string> Wrap(string text, int perLine, int maxLines)
    {
        var lines = new List<string>();
        var current = new StringBuilder();
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.Length > 0 && current.Length + 1 + word.Length > perLine)
            {
                lines.Add(current.ToString());
                current.Clear();
            }

            if (current.Length > 0)
            {
                current.Append(' ');
            }

            current.Append(word);
        }

        if (current.Length > 0)
        {
            lines.Add(current.ToString());
        }

        if (lines.Count > maxLines)
        {
            var rest = string.Join(' ', lines.Skip(maxLines - 1));
            lines = [.. lines.Take(maxLines - 1), Shorten(rest, perLine)];
        }

        return lines.Select(line => Shorten(line, perLine + 6)).ToList();
    }
}
