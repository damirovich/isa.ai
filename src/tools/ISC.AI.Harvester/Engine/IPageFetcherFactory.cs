namespace ISC.AI.Harvester.Engine;

/// <summary>
/// Фабрика получателей HTML (Э4-15): по <see cref="RenderMode"/> из конфига создаёт нужный
/// <see cref="IPageFetcher"/> на прогон сбора. Вызывающий ОБЯЗАН освободить полученный fetcher
/// (headless держит браузер) — через <c>await using</c>.
/// </summary>
public interface IPageFetcherFactory
{
    /// <summary>Создаёт получателя для режима: <see cref="RenderMode.Static"/> — HTTP; <see cref="RenderMode.Headless"/> — браузер.</summary>
    Task<IPageFetcher> CreateAsync(RenderMode mode, CancellationToken cancellationToken = default);
}
