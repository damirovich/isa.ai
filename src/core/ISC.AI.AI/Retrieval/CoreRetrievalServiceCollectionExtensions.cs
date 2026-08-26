using System.Globalization;
using ISC.AI.AI.Security;
using ISC.AI.Abstractions.Retrieval;
using ISC.AI.Abstractions.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ISC.AI.AI.Retrieval;

/// <summary>Регистрация слоя извлечения (RAG-retriever) ядра в контейнере хоста.</summary>
public static class CoreRetrievalServiceCollectionExtensions
{
    /// <summary>
    /// Регистрирует <see cref="IRetriever"/> (pgvector), политику доступа по умолчанию и пороги
    /// извлечения (<see cref="RetrievalOptions"/>, ТО-мат-04, секция конфигурации <c>Retrieval</c>).
    /// Профиль может переопределить <see cref="IAccessPolicy"/>, зарегистрировав свою реализацию (ADR-0014).
    /// </summary>
    public static IServiceCollection AddCoreRetrieval(this IServiceCollection services, IConfiguration configuration)
    {
        // Политика по умолчанию — без доп. ограничений сверх ядрового floor'а (профиль может заменить).
        services.TryAddSingleton<IAccessPolicy, AllowAllAccessPolicy>();
        services.AddSingleton(ReadRetrievalOptions(configuration));
        services.AddScoped<IRetriever, PgVectorRetriever>();
        return services;
    }

    // Порог и метрика ранжирования — из секции Retrieval (ТО-мат-04). MaxDistance не задан/не парсится →
    // отсечения нет; Metric не задан/не парсится → Cosine (совпадает с HNSW-индексом); HnswEfSearch не
    // задан/не парсится → 200 (см. RetrievalOptions: дефолт pgvector 40 пропускает «острова», ТБ-022).
    private static RetrievalOptions ReadRetrievalOptions(IConfiguration configuration)
    {
        double? maxDistance = double.TryParse(
            configuration["Retrieval:MaxDistance"], NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

        var metric = Enum.TryParse<RetrievalMetric>(configuration["Retrieval:Metric"], ignoreCase: true, out var m)
            ? m
            : RetrievalMetric.Cosine;

        var hnswEfSearch = int.TryParse(
            configuration["Retrieval:HnswEfSearch"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ef)
            ? ef
            : 200;

        return new RetrievalOptions(maxDistance, metric, hnswEfSearch);
    }
}
