using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json.Nodes;

namespace ISC.AI.AI.Models;

/// <summary>
/// Политика HTTP-конвейера OpenAI-клиента — совместимость с локальным llama-server:
/// (1) дублирует лимит длины ответа СТАРЫМ полем <c>max_tokens</c> рядом с новым
/// <c>max_completion_tokens</c>; (2) по флагу отключает «размышления» модели
/// (<c>chat_template_kwargs.enable_thinking=false</c>).
/// </summary>
/// <remarks>
/// ЗАЧЕМ (инциденты 2026-08-24):
/// <list type="number">
/// <item>SDK шлёт лимит только новым полем, а llama-server его ИГНОРИРУЕТ (проверено:
/// <c>max_completion_tokens=32</c> → 1155 токенов, <c>max_tokens=32</c> → 32). Без потолка
/// генерация длится десятки минут и умирает по таймауту как «сервер недоступен» (ТН-003).</item>
/// <item>«Думающая» модель тратит лимит на РАЗМЫШЛЕНИЯ: на редакторской задаче — 6 500 токенов
/// раздумий против 209 символов ответа (288 с), на длинном документе размышления съедают лимит
/// целиком и ответ приходит ПУСТЫМ. С отключением — тот же ответ за 2 с. Отключение управляется
/// конфигом <c>Llm:DisableThinking</c> (по умолчанию ВКЛЮЧЕНО отключение: деловым документам
/// скорость и предсказуемость важнее скрытых раздумий; вернуть размышления — false в конфиге).</item>
/// </list>
/// Тело переписывается на уровне конвейера — одно место на все сценарии (генератор, методики,
/// редактор, чат). Запросы без поля <c>messages</c> (эмбеддинги и пр.) не трогаются.
/// </remarks>
internal sealed class LegacyMaxTokensPolicy(bool disableThinking) : PipelinePolicy
{
    /// <inheritdoc />
    public override void Process(
        PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Rewrite(message);
        ProcessNext(message, pipeline, currentIndex);
    }

    /// <inheritdoc />
    public override async ValueTask ProcessAsync(
        PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        Rewrite(message);
        await ProcessNextAsync(message, pipeline, currentIndex);
    }

    private void Rewrite(PipelineMessage message)
    {
        if (message.Request?.Content is not { } content)
        {
            return;
        }

        using var buffer = new MemoryStream();
        content.WriteTo(buffer);
        if (RewriteBody(BinaryData.FromBytes(buffer.ToArray()), disableThinking) is { } rewritten)
        {
            message.Request.Content = BinaryContent.Create(rewritten);
        }
    }

    /// <summary>
    /// Переписывает тело чат-запроса: дублирует <c>max_completion_tokens</c> старым полем
    /// <c>max_tokens</c> и (по флагу) отключает размышления. <see langword="null"/> — переписывать
    /// нечего (не JSON-объект, не чат-запрос или всё уже на месте).
    /// </summary>
    internal static BinaryData? RewriteBody(BinaryData body, bool disableThinking)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(body);
        }
        catch (System.Text.Json.JsonException)
        {
            return null; // не JSON (другой формат запроса) — не наше дело.
        }

        if (root is not JsonObject json)
        {
            return null;
        }

        var changed = false;

        if (json["max_completion_tokens"] is JsonValue limit && !json.ContainsKey("max_tokens"))
        {
            json["max_tokens"] = limit.DeepClone();
            changed = true;
        }

        // Отключение размышлений — только чат-запросам (есть messages) и не перетирая явную настройку.
        if (disableThinking && json.ContainsKey("messages") && !json.ContainsKey("chat_template_kwargs"))
        {
            json["chat_template_kwargs"] = new JsonObject { ["enable_thinking"] = false };
            changed = true;
        }

        return changed ? BinaryData.FromString(json.ToJsonString()) : null;
    }
}
