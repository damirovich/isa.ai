using Microsoft.Playwright;

namespace ISC.AI.Harvester.Engine;

/// <summary>
/// Headless-получатель HTML (Э4-15, <see cref="RenderMode.Headless"/>): загружает страницу в безголовом
/// Chromium (Playwright), ВЫПОЛНЯЕТ JavaScript и отдаёт отрисованный DOM. Нужен для SPA-источников
/// (ЦБД Минюста), где статический HTML пуст. Браузер запускается ОДИН РАЗ на прогон и переиспользуется
/// для всех страниц (запуск дорог), освобождается в <see cref="DisposeAsync"/>.
/// </summary>
/// <remarks>
/// Только вне контура — инструмент подготовки данных; air-gap не нарушается (браузер и Playwright
/// присутствуют лишь в проекте сборщика). Бинарь Chromium ставится один раз: <c>playwright install chromium</c>.
/// </remarks>
public sealed class HeadlessPageFetcher : IPageFetcher
{
    // Реалистичный desktop-UA: многие сайты (в т.ч. ЦБД Минюста) отдают ПУСТУЮ страницу дефолтному
    // «HeadlessChrome» (бот-защита). Без этого рендер возвращает оболочку без контента → сбор пуст.
    private const string DesktopUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/149.0.0.0 Safari/537.36";

    private readonly IPlaywright _playwright;
    private readonly IBrowser _browser;
    private readonly IPage _page;

    private HeadlessPageFetcher(IPlaywright playwright, IBrowser browser, IPage page)
    {
        _playwright = playwright;
        _browser = browser;
        _page = page;
    }

    /// <summary>Запускает безголовый Chromium и открывает страницу для переиспользования на прогон сбора.</summary>
    public static async Task<HeadlessPageFetcher> LaunchAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var playwright = await Playwright.CreateAsync();
        var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        var page = await browser.NewPageAsync(new BrowserNewPageOptions { UserAgent = DesktopUserAgent });
        return new HeadlessPageFetcher(playwright, browser, page);
    }

    /// <inheritdoc />
    public async Task<string> GetHtmlAsync(string url, string? readySelector = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Ждём завершения сетевой активности — SPA успевает подгрузить и отрисовать контент.
        await _page.GotoAsync(url, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        // Если задан селектор готовности — дожидаемся именно контентного узла (надёжнее network-idle).
        if (!string.IsNullOrWhiteSpace(readySelector))
        {
            await _page.WaitForSelectorAsync(readySelector);
        }

        return await _page.ContentAsync();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _browser.DisposeAsync();
        _playwright.Dispose();
    }
}
