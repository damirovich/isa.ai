using ISC.AI.AI.Security;
using ISC.AI.Abstractions.Retrieval;
using ISC.AI.Abstractions.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ISC.AI.AI.Retrieval;

/// <summary>Регистрация слоя извлечения (RAG-retriever) ядра в контейнере хоста.</summary>
public static class CoreRetrievalServiceCollectionExtensions
{
    /// <summary>
    /// Регистрирует <see cref="IRetriever"/> (pgvector) и политику доступа по умолчанию. Профиль
    /// может переопределить <see cref="IAccessPolicy"/>, зарегистрировав свою реализацию (ADR-0014).
    /// </summary>
    public static IServiceCollection AddCoreRetrieval(this IServiceCollection services)
    {
        // Политика по умолчанию — без доп. ограничений сверх ядрового floor'а (профиль может заменить).
        services.TryAddSingleton<IAccessPolicy, AllowAllAccessPolicy>();
        services.AddScoped<IRetriever, PgVectorRetriever>();
        return services;
    }
}
