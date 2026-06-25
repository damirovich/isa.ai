using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using ISC.AI.Abstractions.Harvesting;
using ISC.AI.Harvester.Engine;

namespace ISC.AI.Harvester.Connectors;

/// <summary>
/// Коннектор «сайт по правилам» (ADR-0015): по <see cref="SiteRules"/> из конфига обходит список →
/// ссылки → карточки документов (+ пагинация), извлекая поля селекторами. Любой структурированный
/// сайт (gov.kg и др.) — это КОНФИГ правил, а не отдельный класс.
/// </summary>
public sealed class ConfigurableSiteConnector(HttpClient httpClient) : ISourceConnector
{
    private static readonly HtmlParser Parser = new();

    /// <inheritdoc />
    public string Id => "configurable-site";

    /// <inheritdoc />
    public string DisplayName => "Сайт по правилам (селекторы из конфига)";

    /// <inheritdoc />
    public async IAsyncEnumerable<HarvestedDocument> HarvestAsync(
        SourceConfig config, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        var rules = config.Rules
            ?? throw new InvalidOperationException("ConfigurableSiteConnector требует SiteRules в SourceConfig.Rules.");

        string? listUrl = config.SeedUrl;
        var collected = 0;

        for (var page = 0; page < rules.MaxPages && listUrl is not null && collected < config.MaxDocuments; page++)
        {
            var listDocument = Parser.ParseDocument(await httpClient.GetStringAsync(listUrl, cancellationToken));

            var documentUrls = listDocument.QuerySelectorAll(rules.ItemLinkSelector)
                .Select(element => element.GetAttribute("href"))
                .Where(href => !string.IsNullOrWhiteSpace(href))
                .Select(href => Absolute(listUrl!, href!))
                .ToList();

            foreach (var documentUrl in documentUrls)
            {
                if (collected >= config.MaxDocuments)
                {
                    break;
                }

                var document = Parser.ParseDocument(await httpClient.GetStringAsync(documentUrl, cancellationToken));

                var title = SelectText(document, rules.TitleSelector) ?? document.Title ?? documentUrl;
                var rawText = SelectText(document, rules.BodySelector) ?? document.Body?.TextContent ?? string.Empty;
                var text = TextNormalizer.Collapse(rawText);

                collected++;
                yield return new HarvestedDocument(
                    SourceUrl: documentUrl,
                    Title: title.Trim(),
                    Text: text,
                    DocType: config.DocType,
                    ContentHash: Hash(text),
                    Classification: config.Classification,
                    DivisionId: config.DivisionId,
                    Language: config.Language);
            }

            listUrl = NextPage(listDocument, rules.NextPageSelector, listUrl!);
        }
    }

    private static string? SelectText(IDocument document, string? selector) =>
        string.IsNullOrWhiteSpace(selector) ? null : document.QuerySelector(selector)?.TextContent;

    private static string? NextPage(IDocument listDocument, string? selector, string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(selector))
        {
            return null;
        }

        var href = listDocument.QuerySelector(selector)?.GetAttribute("href");
        return string.IsNullOrWhiteSpace(href) ? null : Absolute(baseUrl, href);
    }

    private static string Absolute(string baseUrl, string href) =>
        Uri.TryCreate(new Uri(baseUrl), href, out var absolute) ? absolute.ToString() : href;

    private static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
