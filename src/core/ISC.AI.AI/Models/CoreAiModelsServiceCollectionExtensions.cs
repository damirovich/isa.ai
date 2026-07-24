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
/// модель/инстанс (роль <see cref="ModelRole.Embeddings"/>, ADR-0011). Каждый keyed-клиент обёрнут
/// таймаутом и circuit breaker (ТН-003, ТНД-001) — см. <see cref="ResilientChatClient"/>,
/// <see cref="ResilientEmbeddingGenerator"/>; блокирующие вызовы — ещё и повтором транзиентных сбоев.
/// Потоковый путь (<c>IChatClient.GetStreamingResponseAsync</c>) защищён circuit breaker и таймаутом
/// бездействия между чанками, но БЕЗ повтора (частично отданный поток перезапускать нельзя).
/// </remarks>
public static class CoreAiModelsServiceCollectionExtensions
{
    // Таймаут одного вызова (ТН-003): генерация (черновик/анализ) может быть долгой, эмбеддинг — короткий.
    private static readonly TimeSpan ChatCallTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan EmbeddingCallTimeout = TimeSpan.FromSeconds(30);

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
        {
            IChatClient client = new OpenAIClient(
                    new ApiKeyCredential(apiKey),
                    new OpenAIClientOptions { Endpoint = new Uri(endpoint) })
                .GetChatClient(model)
                .AsIChatClient();
            return new ResilientChatClient(client, role, ChatCallTimeout);
        });
    }

    private static void TryAddEmbeddingModel(IServiceCollection services, IConfiguration configuration, ModelRole role)
    {
        if (!TryReadModel(configuration, role, out var endpoint, out var model, out var apiKey))
        {
            return;
        }

        services.AddKeyedSingleton<IEmbeddingGenerator<string, Embedding<float>>>(role, (_, _) =>
        {
            IEmbeddingGenerator<string, Embedding<float>> client = new OpenAIClient(
                    new ApiKeyCredential(apiKey),
                    new OpenAIClientOptions { Endpoint = new Uri(endpoint) })
                .GetEmbeddingClient(model)
                .AsIEmbeddingGenerator();
            return new ResilientEmbeddingGenerator(client, role, EmbeddingCallTimeout);
        });
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
