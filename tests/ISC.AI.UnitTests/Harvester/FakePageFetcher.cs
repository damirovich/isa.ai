using ISC.AI.Harvester.Engine;

namespace ISC.AI.UnitTests.Harvester;

/// <summary>
/// Тестовый получатель HTML: отдаёт заранее заданный HTML по URL. Эмулирует и статику, и «отрисованный»
/// headless-DOM — коннектор извлекает документы одинаково, независимо от способа получения (Э4-15).
/// </summary>
internal sealed class FakePageFetcher(IReadOnlyDictionary<string, string> pages) : IPageFetcher
{
    public Task<string> GetHtmlAsync(string url, string? readySelector = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(pages.TryGetValue(url, out var html) ? html : "<html><body>404</body></html>");

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Тестовая фабрика: возвращает <see cref="FakePageFetcher"/> и запоминает запрошенный режим (для проверки выбора headless).</summary>
internal sealed class FakePageFetcherFactory(IReadOnlyDictionary<string, string> pages) : IPageFetcherFactory
{
    public RenderMode? LastRequestedMode { get; private set; }

    public Task<IPageFetcher> CreateAsync(RenderMode mode, CancellationToken cancellationToken = default)
    {
        LastRequestedMode = mode;
        return Task.FromResult<IPageFetcher>(new FakePageFetcher(pages));
    }
}
