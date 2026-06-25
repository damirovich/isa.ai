using ISC.AI.Abstractions.Ingestion;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ISC.AI.Ingestion;

/// <summary>Регистрация конвейера загрузки (ingestion) ядра в контейнере хоста.</summary>
public static class CoreIngestionServiceCollectionExtensions
{
    /// <summary>
    /// Регистрирует <see cref="IIngestionPort"/> (fail-closed загрузка) и чанкер по умолчанию.
    /// Стратегию чанкинга можно заменить, зарегистрировав свою <see cref="ITextChunker"/>.
    /// </summary>
    public static IServiceCollection AddCoreIngestion(this IServiceCollection services)
    {
        services.TryAddSingleton<ITextChunker, SimpleTextChunker>();
        services.AddScoped<IIngestionPort, IngestionPort>();
        // Загрузка из файла (извлечение текста → порт); требует ITextExtractor из AddCoreDocuments.
        services.AddScoped<IFileIngestor, FileIngestionService>();
        // Импорт пакета сборщика (manifest.json → порт); в-контурная сторона Harvester (Э4-08).
        services.AddScoped<IBundleImporter, BundleImporter>();
        return services;
    }
}
