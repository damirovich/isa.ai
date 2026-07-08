namespace ISC.AI.Harvester.Engine;

/// <summary>
/// Реализация <see cref="IPageFetcherFactory"/>: <see cref="RenderMode.Static"/> → обёртка вокруг
/// разделяемого <see cref="HttpClient"/>; <see cref="RenderMode.Headless"/> → запуск headless-Chromium
/// (Playwright) на прогон. Headless-браузер освобождает вызывающий (через <c>await using</c>).
/// </summary>
public sealed class PageFetcherFactory(HttpClient httpClient) : IPageFetcherFactory
{
    /// <inheritdoc />
    public async Task<IPageFetcher> CreateAsync(RenderMode mode, CancellationToken cancellationToken = default) =>
        mode switch
        {
            RenderMode.Headless => await HeadlessPageFetcher.LaunchAsync(cancellationToken),
            _ => new HttpPageFetcher(httpClient),
        };
}
