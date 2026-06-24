using ISC.AI.Abstractions.Rag;
using Microsoft.Extensions.DependencyInjection;

namespace ISC.AI.AI.Rag;

/// <summary>Регистрация RAG-оркестратора ядра в контейнере хоста.</summary>
public static class CoreRagServiceCollectionExtensions
{
    /// <summary>
    /// Регистрирует <see cref="IGroundedGenerator"/>. Зависит от <c>IRetriever</c> (AddCoreRetrieval),
    /// <c>IGroundingValidator</c> (AddCoreGrounding), keyed <c>IChatClient</c> (AddCoreAiModels) и
    /// <c>IAuditWriter</c> (AddCorePersistence) — их регистрирует хост до этого вызова.
    /// </summary>
    public static IServiceCollection AddCoreRag(this IServiceCollection services)
    {
        services.AddScoped<IGroundedGenerator, GroundedGenerator>();
        return services;
    }
}
