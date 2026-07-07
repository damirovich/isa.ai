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

        // Реестр коннекторов: оба доступны как ISourceConnector (UI выбирает по Id/DisplayName).
        services.AddTransient<ISourceConnector>(sp => sp.GetRequiredService<GenericUrlConnector>());
        services.AddTransient<ISourceConnector>(sp => sp.GetRequiredService<ConfigurableSiteConnector>());

        return services;
    }

    private static void ConfigureClient(HttpClient client)
    {
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ISC.AI.Harvester/1.0");
        client.Timeout = TimeSpan.FromSeconds(30);
    }
}
