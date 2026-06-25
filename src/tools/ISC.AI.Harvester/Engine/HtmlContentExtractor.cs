using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace ISC.AI.Harvester.Engine;

/// <summary>
/// Универсальный извлекатель основного текста из HTML (AngleSharp): отбрасывает навигацию/скрипты/стили,
/// берёт заголовок и текст тела. Работает на ЛЮБОЙ странице best-effort (ADR-0015) — точные метаданные
/// дают правила <see cref="SiteRules"/>.
/// </summary>
public sealed class HtmlContentExtractor : IContentExtractor
{
    private static readonly HtmlParser Parser = new();

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

        var bodyText = document.Body?.TextContent ?? string.Empty;
        return new ExtractedContent((title ?? string.Empty).Trim(), TextNormalizer.Collapse(bodyText));
    }
}
