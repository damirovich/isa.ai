namespace ISC.AI.Harvester.Engine;

/// <summary>
/// Получатель HTML страницы (Э4-15): отделяет «КАК достать HTML» от «ЧТО с ним делать» (селекторы).
/// Реализации — прямой HTTP (<see cref="HttpPageFetcher"/>) и headless-браузер (<see cref="HeadlessPageFetcher"/>)
/// для SPA. Одноразовый на прогон сбора (headless держит браузер — освобождается через <see cref="IAsyncDisposable"/>).
/// </summary>
public interface IPageFetcher : IAsyncDisposable
{
    /// <summary>Возвращает HTML страницы.</summary>
    /// <param name="url">Абсолютный URL страницы.</param>
    /// <param name="readySelector">Для headless: селектор контентного узла, отрисовки которого дождаться (иначе — network-idle).</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    Task<string> GetHtmlAsync(string url, string? readySelector = null, CancellationToken cancellationToken = default);
}
