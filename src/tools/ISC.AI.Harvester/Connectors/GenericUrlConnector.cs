using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using ISC.AI.Abstractions.Harvesting;
using ISC.AI.Harvester.Engine;

namespace ISC.AI.Harvester.Connectors;

/// <summary>
/// Универсальный коннектор: скачивает ОДНУ страницу по URL и извлекает текст best-effort (ADR-0015).
/// Доступен всегда; для точных метаданных — адаптеры источников. Обход по ссылкам — будущее расширение.
/// </summary>
public sealed class GenericUrlConnector(IPageFetcherFactory fetcherFactory, IContentExtractor extractor) : ISourceConnector
{
    /// <inheritdoc />
    public string Id => "generic-url";

    /// <inheritdoc />
    public string DisplayName => "Произвольный URL";

    /// <inheritdoc />
    public async IAsyncEnumerable<HarvestedDocument> HarvestAsync(
        SourceConfig config, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);

        // Одиночный URL — статический режим (для SPA-обхода используется ConfigurableSiteConnector с RenderMode.Headless).
        await using var fetcher = await fetcherFactory.CreateAsync(RenderMode.Static, cancellationToken);
        var html = await fetcher.GetHtmlAsync(config.SeedUrl, cancellationToken: cancellationToken);
        var content = extractor.Extract(html, config.SeedUrl);

        yield return new HarvestedDocument(
            SourceUrl: config.SeedUrl,
            Title: string.IsNullOrWhiteSpace(content.Title) ? config.SeedUrl : content.Title,
            Text: content.Text,
            DocType: config.DocType,
            ContentHash: Hash(content.Text),
            Classification: config.Classification,
            DivisionId: null, // выбирается при импорте внутри контура (SourceConfig)
            Language: config.Language);
    }

    private static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
