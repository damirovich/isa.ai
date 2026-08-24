using System.ClientModel;
using System.Text.Json;
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
    // Таймауты одного вызова (ТН-003) — ДЕФОЛТЫ, переопределяются конфигом Llm:ChatCallTimeoutSeconds /
    // Llm:EmbeddingCallTimeoutSeconds (скорость локального сервера — свойство развёртывания, не кода).
    // Чат: полный ответ в MaxOutputTokens=4096 на локальной модели ~15-20 ток/с занимает 3-5 минут —
    // 120 с обрывали ДЛИННУЮ генерацию (методика/чек-лист) как «сервер недоступен» при живом сервере
    // (2026-08-24, та же болезнь, что у эмбеддера 30 с → 120 с при импорте кодекса 2026-08-19).
    // Долгая неудачная попытка держит bulkhead-слот дольше — осознанная цена: мёртвый сервер падает
    // быстро (connection refused), по таймауту ждёт только ЖИВОЙ медленный, где ожидание оправдано.
    private static readonly TimeSpan DefaultChatCallTimeout = TimeSpan.FromSeconds(3600);
    private static readonly TimeSpan DefaultEmbeddingCallTimeout = TimeSpan.FromSeconds(1200);

    // Bulkhead по умолчанию: не больше N одновременных вызовов на роль к общему серверу инференса.
    private const int DefaultMaxConcurrencyPerRole = 4;

    /// <summary>Регистрирует keyed-клиенты ролей draft/analysis (чат) и embeddings из конфигурации.</summary>
    public static IServiceCollection AddCoreAiModels(this IServiceCollection services, IConfiguration configuration)
    {
        // Лимит параллелизма к серверу инференса (Llm:MaxConcurrencyPerRole) — общий для ролей; защита GPU-сервера.
        // Чтение через индексатор (как и адреса моделей): не задано/пусто/некорректно → безопасный дефолт.
        var maxConcurrency = int.TryParse(configuration["Llm:MaxConcurrencyPerRole"], out var configured) && configured > 0
            ? configured
            : DefaultMaxConcurrencyPerRole;

        // Таймауты вызовов — из конфига, тем же безопасным чтением: не задано/некорректно → дефолт.
        var chatTimeout = ReadTimeout(configuration, "Llm:ChatCallTimeoutSeconds", DefaultChatCallTimeout);
        var embeddingTimeout = ReadTimeout(configuration, "Llm:EmbeddingCallTimeoutSeconds", DefaultEmbeddingCallTimeout);

        TryAddChatModel(services, configuration, ModelRole.Draft, maxConcurrency, chatTimeout);
        TryAddChatModel(services, configuration, ModelRole.Analysis, maxConcurrency, chatTimeout);
        TryAddEmbeddingModel(services, configuration, ModelRole.Embeddings, maxConcurrency, embeddingTimeout);
        return services;
    }

    private static TimeSpan ReadTimeout(IConfiguration configuration, string key, TimeSpan fallback) =>
        int.TryParse(configuration[key], out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : fallback;

    private static void TryAddChatModel(
        IServiceCollection services, IConfiguration configuration, ModelRole role, int maxConcurrency, TimeSpan callTimeout)
    {
        if (!TryReadModel(configuration, role, out var endpoint, out var configuredModel, out var apiKey))
        {
            return; // роль не сконфигурирована (нет адреса) — пропускаем
        }

        // Air-gap (инвариант №2, ТБ-044): адрес модели обязан быть внутри контура — проверяем при старте.
        var endpointUri = new Uri(endpoint);
        AirGapEndpointGuard.EnsureWithinPerimeter(endpointUri, role);

        services.AddKeyedSingleton<IChatClient>(role, (_, _) =>
        {
            // Имя модели: явное из конфига, иначе — авто-определение из /v1/models (первый ход, кешируется в singleton).
            var model = ResolveModelName(endpoint, configuredModel, role);
            IChatClient client = new OpenAIClient(
                    new ApiKeyCredential(apiKey),
                    BuildClientOptions(endpointUri))
                .GetChatClient(model)
                .AsIChatClient();
            return new ResilientChatClient(client, role, callTimeout, maxConcurrency);
        });
    }

    private static void TryAddEmbeddingModel(
        IServiceCollection services, IConfiguration configuration, ModelRole role, int maxConcurrency, TimeSpan callTimeout)
    {
        if (!TryReadModel(configuration, role, out var endpoint, out var configuredModel, out var apiKey))
        {
            return;
        }

        // Air-gap (инвариант №2, ТБ-044): адрес эмбеддера обязан быть внутри контура — проверяем при старте.
        var endpointUri = new Uri(endpoint);
        AirGapEndpointGuard.EnsureWithinPerimeter(endpointUri, role);

        services.AddKeyedSingleton<IEmbeddingGenerator<string, Embedding<float>>>(role, (_, _) =>
        {
            var model = ResolveModelName(endpoint, configuredModel, role);
            IEmbeddingGenerator<string, Embedding<float>> client = new OpenAIClient(
                    new ApiKeyCredential(apiKey),
                    BuildClientOptions(endpointUri))
                .GetEmbeddingClient(model)
                .AsIEmbeddingGenerator();
            return new ResilientEmbeddingGenerator(client, role, callTimeout, maxConcurrency);
        });
    }

    /// <summary>
    /// Общие настройки OpenAI-клиента (инцидент 2026-08-24, ТН-003):
    /// <list type="bullet">
    /// <item>СЕТЕВОЙ таймаут SDK отключён (дефолт 100 с рвал любой вызов длиннее — «даже 3600 не
    /// хватило»): единственный владелец таймаута — наша обвязка (<see cref="ModelCallResilience"/>,
    /// значение из конфига на попытку); двух конкурирующих таймаутов быть не должно.</item>
    /// <item>ВНУТРЕННИЙ повтор SDK отключён (по умолчанию ×4): повторы уже делает та же обвязка —
    /// иначе они перемножались (4×3 = 12 обречённых попыток ≈ 20 минут на один клик).</item>
    /// <item>Политика <see cref="LegacyMaxTokensPolicy"/>: лимит длины ответа дублируется старым
    /// полем <c>max_tokens</c> — llama-server игнорирует новое имя поля, и без потолка «думающая»
    /// модель генерирует бесконечно.</item>
    /// </list>
    /// </summary>
    private static OpenAIClientOptions BuildClientOptions(Uri endpointUri)
    {
        var options = new OpenAIClientOptions
        {
            Endpoint = endpointUri,
            NetworkTimeout = System.Threading.Timeout.InfiniteTimeSpan,
            RetryPolicy = new System.ClientModel.Primitives.ClientRetryPolicy(maxRetries: 0),
        };
        options.AddPolicy(new LegacyMaxTokensPolicy(), System.ClientModel.Primitives.PipelinePosition.PerCall);
        return options;
    }

    // Адрес/модель/ключ роли — из секции Llm:Models:{role}. У локального сервера авторизации нет → ключ-заглушка.
    // Роль считается сконфигурированной по наличию АДРЕСА; имя модели может быть пустым → авто-определение.
    private static bool TryReadModel(
        IConfiguration configuration, ModelRole role,
        out string endpoint, out string model, out string apiKey)
    {
        var section = $"Llm:Models:{role}";
        endpoint = configuration[$"{section}:Endpoint"] ?? string.Empty;
        model = configuration[$"{section}:Model"] ?? string.Empty;
        apiKey = configuration[$"{section}:ApiKey"] is { Length: > 0 } key ? key : "no-key-needed";
        return !string.IsNullOrWhiteSpace(endpoint);
    }

    /// <summary>
    /// Имя модели для запроса: ЯВНОЕ из конфигурации (уважается как есть, для строгих контуров с фиксированной
    /// моделью), иначе — АВТО-ОПРЕДЕЛЕНИЕ из <c>{endpoint}/models</c> локального сервера (берётся первый
    /// загруженный id). Убирает рассинхрон «в конфиге одно имя, а на сервере загружен другой id» — сервер сам
    /// сообщает, что у него загружено. Обращение к <c>/models</c> идёт на ТОТ ЖЕ адрес внутри контура, что и
    /// инференс (air-gap не нарушается — адрес уже проверен <see cref="AirGapEndpointGuard"/>).
    /// </summary>
    internal static string ResolveModelName(string endpoint, string configuredModel, ModelRole role)
    {
        if (!string.IsNullOrWhiteSpace(configuredModel))
        {
            return configuredModel;
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            var json = http.GetStringAsync($"{endpoint.TrimEnd('/')}/models").GetAwaiter().GetResult();
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("data", out var data) && data.GetArrayLength() > 0
                && data[0].TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } modelId)
            {
                return modelId;
            }

            throw new InvalidOperationException($"Сервер «{endpoint}/models» не вернул ни одной модели.");
        }
        catch (Exception exception) when (exception is not InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"Не удалось определить модель роли «{role}» из «{endpoint}/models», и имя не задано в конфигурации "
                + $"(Llm:Models:{role}:Model). Проверьте, что сервер инференса запущен, либо укажите имя модели явно. "
                + exception.Message,
                exception);
        }
    }
}
