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

    // Порог — из Retrieval:MaxDistance. Не задан/не парсится → отсечения нет (см. RetrievalOptions.None).
    private static RetrievalOptions ReadRetrievalOptions(IConfiguration configuration)
    {
        var raw = configuration["Retrieval:MaxDistance"];
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var maxDistance)
            ? new RetrievalOptions(maxDistance)
            : RetrievalOptions.None;
    }
}
