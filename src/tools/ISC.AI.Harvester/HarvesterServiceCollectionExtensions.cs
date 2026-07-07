using ISC.AI.Harvester.Connectors;
using ISC.AI.Harvester.Engine;
using Microsoft.Extensions.DependencyInjection;

namespace ISC.AI.Harvester;

/// <summary>Регистрация движка сборщика (вне контура): экстрактор, запись пакета, коннекторы.</summary>
public static class HarvesterServiceCollectionExtensions
{
    /// <summary>Регистрирует движок и доступные коннекторы (generic-URL — по умолчанию).</summary>
    public static IServiceCollection AddHarvesterEngine(this IServiceCollection services)
    {
        services.AddSingleton<IContentExtractor, HtmlContentExtractor>();
        services.AddSingleton<IBundleWriter, JsonBundleWriter>();

        // Получатель HTML: HTTP (статические сайты) или headless-браузер (SPA) — выбор по RenderMode (Э4-15).
        // Типизированный HttpClient (вежливый User-Agent, таймаут) — на статический путь фабрики.
        services.AddHttpClient<PageFetcherFactory>(ConfigureClient);
        services.AddTransient<IPageFetcherFactory>(sp => sp.GetRequiredService<PageFetcherFactory>());

        services.AddTransient<GenericUrlConnector>();
        services.AddTransient<ConfigurableSiteConnector>();

        // API-коннектор ЦБД Минюста (Э4-16): свой HttpClient с реалистичным UA (API режет дефолтный бот-UA).
        services.AddHttpClient<CbdApiConnector>(ConfigureBrowserClient);

        // Реестр коннекторов: все доступны как ISourceConnector (UI выбирает по Id/DisplayName).
        services.AddTransient<ISourceConnector>(sp => sp.GetRequiredService<GenericUrlConnector>());
        services.AddTransient<ISourceConnector>(sp => sp.GetRequiredService<ConfigurableSiteConnector>());
        services.AddTransient<ISourceConnector>(sp => sp.GetRequiredService<CbdApiConnector>());

        return services;
    }

    private static void ConfigureClient(HttpClient client)
    {
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ISC.AI.Harvester/1.0");
        client.Timeout = TimeSpan.FromSeconds(30);
    }

    // Реалистичный desktop-UA: сайты/API с бот-защитой отдают данные браузеру, но режут дефолтный UA.
    private static void ConfigureBrowserClient(HttpClient client)
    {
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/149.0.0.0 Safari/537.36");
        client.Timeout = TimeSpan.FromSeconds(60);
    }
}
