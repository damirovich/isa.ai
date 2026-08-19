using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace ISC.AI.Harvester.Engine;

/// <summary>
/// Универсальный извлекатель основного текста из HTML (AngleSharp): отбрасывает навигацию/скрипты/стили,
/// берёт заголовок и текст тела. Работает на ЛЮБОЙ странице best-effort (ADR-0015) — точные метаданные
/// дают правила <see cref="SiteRules"/>.
/// </summary>
/// <remarks>
/// Текст собирается С СОХРАНЕНИЕМ СТРУКТУРЫ (2026-08-19): блочные элементы (абзацы, заголовки, строки
/// таблиц, элементы списков) дают перенос строки, абзацы разделяются пустой строкой. Раньше брался
/// <c>Body.TextContent</c> и всё схлопывалось в одну строку — слова на стыках блоков склеивались
/// («бюджетеКыргызской»), а чанкер НПА, ищущий «Статья N» в начале строки, структуру не находил
/// и резал текст слепо по длине, посреди слов. Ячейки таблицы разделяются « | » — таблица остаётся
/// читаемой построчно, а не превращается в кашу чисел.
/// </remarks>
public sealed class HtmlContentExtractor : IContentExtractor
{
    private static readonly HtmlParser Parser = new();

    // Элементы, после которых начинается новый абзац (пустая строка между ними).
    private static readonly HashSet<string> ParagraphTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "P", "DIV", "H1", "H2", "H3", "H4", "H5", "H6", "SECTION", "ARTICLE", "BLOCKQUOTE", "PRE",
        "UL", "OL", "TABLE", "TR", "DL",
    };

    // Элементы, после которых достаточно перевода строки (внутри абзаца/списка/таблицы).
    private static readonly HashSet<string> LineTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "BR", "LI", "DT", "DD", "CAPTION", "THEAD", "TBODY", "TFOOT",
    };

    /// <inheritdoc />
    public ExtractedContent Extract(string html, string sourceUrl)
    {
        var document = Parser.ParseDocument(html ?? string.Empty);

        // Убираем неконтентные элементы, чтобы они не попали в текст.
        foreach (var node in document.QuerySelectorAll("script, style, nav, header, footer, noscript, form").ToArray())
        {
            node.Remove();
        }

        var title = document.Title;
        if (string.IsNullOrWhiteSpace(title))
        {
            title = document.QuerySelector("h1")?.TextContent;
        }

        var builder = new StringBuilder();
        if (document.Body is { } body)
        {
            AppendStructured(body, builder);
        }

        return new ExtractedContent((title ?? string.Empty).Trim(), TextNormalizer.NormalizeStructured(builder.ToString()));
    }

    /// <summary>Структурный текст узла: абзацы и строки таблиц сохранены (для коннекторов по правилам).</summary>
    public static string StructuredText(INode node)
    {
        var builder = new StringBuilder();
        AppendStructured(node, builder);
        return TextNormalizer.NormalizeStructured(builder.ToString());
    }

    /// <summary>Обход дерева: текстовые узлы — как есть, блочные границы — переносами.</summary>
    private static void AppendStructured(INode node, StringBuilder builder)
    {
        foreach (var child in node.ChildNodes)
        {
            switch (child)
            {
                case IText text:
                    builder.Append(text.Data);
                    break;

                case IElement element:
                    var tag = element.TagName;
                    if (ParagraphTags.Contains(tag))
                    {
                        builder.Append("\n\n");
                    }
                    else if (LineTags.Contains(tag))
                    {
                        builder.Append('\n');
                    }

                    AppendStructured(element, builder);

                    if (string.Equals(tag, "TD", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(tag, "TH", StringComparison.OrdinalIgnoreCase))
                    {
                        // Ячейки — через разделитель, строка таблицы закроется переносом от TR.
                        builder.Append(" | ");
                    }
                    else if (ParagraphTags.Contains(tag))
                    {
                        builder.Append("\n\n");
                    }
                    else if (LineTags.Contains(tag))
                    {
                        builder.Append('\n');
                    }

                    break;
            }
        }
    }
}
