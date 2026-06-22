using System.ClientModel;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OpenAI;

// Маленький чат-тест: проверяет связь с локальным ИИ (vLLM, OpenAI-совместимый API)
// через наш стек Microsoft.Extensions.AI (IChatClient). Де-риск задачи Э3-04.
//
// Повторяет рабочую ручную проверку:
//   curl -s http://127.0.0.1:9000/v1/chat/completions -H "Content-Type: application/json" \
//        -d '{"messages":[{"role":"user","content":"Кыргыз тилинде өзүң жөнүндө кыскача жаз."}]}'
//
// Запуск:
//   dotnet run --project spikes/LlmChat
//   dotnet run --project spikes/LlmChat -- http://127.0.0.1:9000/v1 "" "Свой вопрос"
// Либо через переменные окружения: LLM_ENDPOINT / LLM_MODEL / LLM_API_KEY.

string endpoint = args.ElementAtOrDefault(0)
    ?? Environment.GetEnvironmentVariable("LLM_ENDPOINT")
    ?? "http://10.10.0.115:9000/v1";
string? model = NullIfBlank(args.ElementAtOrDefault(1))
    ?? Environment.GetEnvironmentVariable("LLM_MODEL");
string prompt = args.ElementAtOrDefault(2)
    ?? "Раскажи о себе. ";
string apiKey = Environment.GetEnvironmentVariable("LLM_API_KEY") ?? "no-key-needed";

// Модель не задана — определяем автоматически из /v1/models (как curl без поля "model").
model ??= await TryGetFirstModelAsync(endpoint) ?? "qwen";

Console.WriteLine($"Эндпоинт: {endpoint}");
Console.WriteLine($"Модель:   {model}");
Console.WriteLine($"Запрос:   {prompt}");
Console.WriteLine(new string('-', 48));

IChatClient chat = new OpenAIClient(
        new ApiKeyCredential(apiKey),
        new OpenAIClientOptions { Endpoint = new Uri(endpoint) })
    .GetChatClient(model)
    .AsIChatClient();

try
{
    // Стриминг — как и положено для длинных вызовов модели (конвенция CLAUDE.md).
    await foreach (var update in chat.GetStreamingResponseAsync(prompt))
    {
        Console.Write(update.Text);
    }

    Console.WriteLine();
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine($"Ошибка обращения к ИИ: {ex.GetType().Name}: {ex.Message}");
    Console.Error.WriteLine(
        "Проверьте, что vLLM запущен и доступен по указанному эндпоинту " +
        "(для WSL/Docker нужен проброс порта в localhost Windows, либо укажите реальный адрес).");
    return 1;
}

static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

// Запрашивает список моделей у /v1/models и возвращает идентификатор первой (или null при ошибке).
static async Task<string?> TryGetFirstModelAsync(string endpoint)
{
    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        var json = await http.GetStringAsync($"{endpoint.TrimEnd('/')}/models");
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("data", out var data) && data.GetArrayLength() > 0)
        {
            return data[0].GetProperty("id").GetString();
        }
    }
    catch
    {
        // Нет связи или иной формат — вернём null, дальше используется значение по умолчанию.
    }

    return null;
}
