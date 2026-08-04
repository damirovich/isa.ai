using ISC.AI.AI.Chat;
using ISC.AI.Abstractions.Conversations;
using ISC.AI.Abstractions.Rag;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ISC.AI.AI.Rag;

/// <summary>Регистрация RAG-оркестратора ядра в контейнере хоста.</summary>
public static class CoreRagServiceCollectionExtensions
{
    /// <summary>
    /// Регистрирует <see cref="IGroundedGenerator"/> и <see cref="GenerationOptions"/> (секция
    /// <c>Llm:Generation</c>). Зависит от <c>IRetriever</c> (AddCoreRetrieval), <c>IGroundingValidator</c>
    /// (AddCoreGrounding), keyed <c>IChatClient</c> (AddCoreAiModels) и <c>IAuditWriter</c>
    /// (AddCorePersistence) — их регистрирует хост до этого вызова.
    /// </summary>
    public static IServiceCollection AddCoreRag(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(ReadGenerationOptions(configuration));
        services.AddScoped<IGroundedGenerator, GroundedGenerator>();

        // Свободный режим чата (общение/помощь без грунтовки; правило запрещает юр-утверждения).
        services.AddScoped<IConversationalGenerator, ConversationalGenerator>();

        // Многоходовый ассистент чата: свободный + грунтованный режимы, история диалога.
        services.AddScoped<IChatService, ChatService>();
        return services;
    }

    // Параметры генерации из секции Llm:Generation. Секции нет → безопасные умолчания (детерминизм, лимит длины).
    private static GenerationOptions ReadGenerationOptions(IConfiguration configuration)
    {
        var section = configuration.GetSection("Llm:Generation");
        if (!section.Exists())
        {
            return GenerationOptions.Default;
        }

        var defaults = GenerationOptions.Default;
        return new GenerationOptions(
            Temperature: section.GetValue("Temperature", defaults.Temperature),
            TopP: section.GetValue<float?>("TopP", null),
            MaxOutputTokens: section.GetValue("MaxOutputTokens", defaults.MaxOutputTokens),
            Seed: section.GetValue<long?>("Seed", null),
            PromptTokenBudget: section.GetValue<int?>("PromptTokenBudget", null),
            CharsPerToken: section.GetValue("CharsPerToken", defaults.CharsPerToken));
    }
}
