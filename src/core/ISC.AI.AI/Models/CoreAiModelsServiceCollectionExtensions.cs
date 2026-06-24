using System.ClientModel;
using ISC.AI.Abstractions.Enums;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenAI;

namespace ISC.AI.AI.Models;

/// <summary>
/// Регистрация локальных моделей за нейтральными контрактами <see cref="IChatClient"/> /
/// <see cref="IEmbeddingGenerator{TInput,TEmbedding}"/> (ADR-0004, ТО-прог-02/03). Клиенты
/// регистрируются KEYED по роли (<see cref="ModelRole"/>); адреса и имена моделей берутся ТОЛЬКО из
/// конфигурации (секция <c>Llm:Models:{role}</c>) — ядро не знает названий моделей и сервера.
/// </summary>
/// <remarks>
/// Канал — локальный OpenAI-совместимый сервер инференса (llama-server) ВНУТРИ контура; авторизации
/// нет (ключ-заглушка), внешних обращений быть не должно (ТБ-044, air-gap). Эмбеддинги — ОТДЕЛЬНАЯ
/// модель/инстанс (роль <see cref="ModelRole.Embeddings"/>, ADR-0011). Длинные вызовы стримятся
/// штатно через <c>IChatClient.GetStreamingResponseAsync</c>.
/// </remarks>
public static class CoreAiModelsServiceCollectionExtensions
{
    /// <summary>Регистрирует keyed-клиенты ролей draft/analysis (чат) и embeddings из конфигурации.</summary>
    public static IServiceCollection AddCoreAiModels(this IServiceCollection services, IConfiguration configuration)
    {
        TryAddChatModel(services, configuration, ModelRole.Draft);
        TryAddChatModel(services, configuration, ModelRole.Analysis);
        TryAddEmbeddingModel(services, configuration, ModelRole.Embeddings);
        return services;
    }

    private static void TryAddChatModel(IServiceCollection services, IConfiguration configuration, ModelRole role)
    {
        if (!TryReadModel(configuration, role, out var endpoint, out var model, out var apiKey))
        {
            return; // роль не сконфигурирована — пропускаем (модель добавляется конфигом, без изменения кода)
        }

        services.AddKeyedSingleton<IChatClient>(role, (_, _) =>
            new OpenAIClient(
                    new ApiKeyCredential(apiKey),
                    new OpenAIClientOptions { Endpoint = new Uri(endpoint) })
                .GetChatClient(model)
                .AsIChatClient());
    }

    private static void TryAddEmbeddingModel(IServiceCollection services, IConfiguration configuration, ModelRole role)
    {
        if (!TryReadModel(configuration, role, out var endpoint, out var model, out var apiKey))
        {
            return;
        }

        services.AddKeyedSingleton<IEmbeddingGenerator<string, Embedding<float>>>(role, (_, _) =>
            new OpenAIClient(
                    new ApiKeyCredential(apiKey),
                    new OpenAIClientOptions { Endpoint = new Uri(endpoint) })
                .GetEmbeddingClient(model)
                .AsIEmbeddingGenerator());
    }

    // Адрес/модель/ключ роли — из секции Llm:Models:{role}. У локального сервера авторизации нет → ключ-заглушка.
    private static bool TryReadModel(
        IConfiguration configuration, ModelRole role,
        out string endpoint, out string model, out string apiKey)
    {
        var section = $"Llm:Models:{role}";
        endpoint = configuration[$"{section}:Endpoint"] ?? string.Empty;
        model = configuration[$"{section}:Model"] ?? string.Empty;
        apiKey = configuration[$"{section}:ApiKey"] is { Length: > 0 } key ? key : "no-key-needed";
        return !string.IsNullOrWhiteSpace(endpoint) && !string.IsNullOrWhiteSpace(model);
    }
}
