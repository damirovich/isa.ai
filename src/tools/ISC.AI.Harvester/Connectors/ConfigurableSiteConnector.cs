using System.Globalization;
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
public sealed class ConfigurableSiteConnector(IPageFetcherFactory fetcherFactory) : ISourceConnector
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

        // Получатель HTML по режиму (Static=HTTP, Headless=браузер с JS) — один на прогон (Э4-15).
        await using var fetcher = await fetcherFactory.CreateAsync(rules.RenderMode, cancellationToken);

        string? listUrl = config.SeedUrl;
        var collected = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal); // дедуп ссылок между страницами

        for (var page = 0; page < rules.MaxPages && listUrl is not null && collected < config.MaxDocuments; page++)
        {
            var listDocument = Parser.ParseDocument(await fetcher.GetHtmlAsync(listUrl, rules.ReadySelector, cancellationToken));

            var documentUrls = listDocument.QuerySelectorAll(rules.ItemLinkSelector)
                .Select(element => element.GetAttribute("href"))
                .Where(href => !string.IsNullOrWhiteSpace(href))
                .Select(href => Absolute(listUrl!, href!))
                .Where(url => seen.Add(url)) // только НОВЫЕ (страница без новых ссылок = конец списка)
                .ToList();

            if (documentUrls.Count == 0)
            {
                break; // новых документов нет — прекращаем пагинацию (иначе крутили бы до MaxPages впустую)
            }

            foreach (var documentUrl in documentUrls)
            {
                if (collected >= config.MaxDocuments)
                {
                    break;
                }

                var document = Parser.ParseDocument(await fetcher.GetHtmlAsync(documentUrl, rules.ReadySelector, cancellationToken));

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

            listUrl = NextPage(listDocument, rules, listUrl!);
        }
    }

    private static string? SelectText(IDocument document, string? selector) =>
        string.IsNullOrWhiteSpace(selector) ? null : document.QuerySelector(selector)?.TextContent;

    private static string? NextPage(IDocument listDocument, SiteRules rules, string currentUrl)
    {
        // 1) «Следующая страница» ссылкой по CSS-селектору — если задан.
        if (!string.IsNullOrWhiteSpace(rules.NextPageSelector))
        {
            var href = listDocument.QuerySelector(rules.NextPageSelector)?.GetAttribute("href");
            return string.IsNullOrWhiteSpace(href) ? null : Absolute(currentUrl, href);
        }

        // 2) Инкремент query-параметра страницы (напр. ?page=2,3…) — если задан.
        if (!string.IsNullOrWhiteSpace(rules.PageParam))
        {
            return IncrementPageParam(currentUrl, rules.PageParam);
        }

        return null; // без пагинации — обрабатываем одну страницу
    }

    private static string IncrementPageParam(string url, string param)
    {
        var uri = new Uri(url);
        var pairs = uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(pair => pair[0], pair => pair.Length > 1 ? pair[1] : string.Empty, StringComparer.OrdinalIgnoreCase);

        var current = pairs.TryGetValue(param, out var value)
            && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 1;
        pairs[param] = (current + 1).ToString(CultureInfo.InvariantCulture);

        var query = string.Join('&', pairs.Select(kv => $"{kv.Key}={kv.Value}"));
        return new UriBuilder(uri) { Query = query }.Uri.ToString();
    }

    private static string Absolute(string baseUrl, string href) =>
        Uri.TryCreate(new Uri(baseUrl), href, out var absolute) ? absolute.ToString() : href;

    private static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
