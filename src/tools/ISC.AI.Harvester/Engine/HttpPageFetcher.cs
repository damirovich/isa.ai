namespace ISC.AI.Harvester.Engine;

/// <summary>
/// Прямой HTTP-получатель HTML (Э4-15, <see cref="RenderMode.Static"/>): текущее поведение сборщика —
/// быстро, без выполнения JavaScript; подходит статическим сайтам (gov.kg и др.). Разделяемый
/// <see cref="HttpClient"/> принадлежит DI/фабрике и здесь НЕ освобождается.
/// </summary>
public sealed class HttpPageFetcher(HttpClient httpClient) : IPageFetcher
{
    /// <inheritdoc />
    public Task<string> GetHtmlAsync(string url, string? readySelector = null, CancellationToken cancellationToken = default) =>
        httpClient.GetStringAsync(url, cancellationToken); // readySelector неприменим: статический HTTP не ждёт отрисовки

    /// <inheritdoc />
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
