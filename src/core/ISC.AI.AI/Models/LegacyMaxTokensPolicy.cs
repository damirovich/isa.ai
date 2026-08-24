using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text.Json.Nodes;

namespace ISC.AI.AI.Models;

/// <summary>
/// Политика HTTP-конвейера OpenAI-клиента: дублирует лимит длины ответа СТАРЫМ полем
/// <c>max_tokens</c> рядом с новым <c>max_completion_tokens</c>.
/// </summary>
/// <remarks>
/// ЗАЧЕМ (инцидент 2026-08-24): SDK шлёт лимит только новым полем, а локальный llama-server его
/// ИГНОРИРУЕТ (проверено: <c>max_completion_tokens=32</c> → 1155 токенов, <c>max_tokens=32</c> → 32).
/// Без действующего потолка «думающая» модель генерирует без ограничения — вызов длится десятки
/// минут и умирает по таймауту как «сервер недоступен» (ТН-003). Публичного переключателя на старое
/// поле в SDK этой версии нет, поэтому тело переписывается на уровне конвейера — одно место на все
/// сценарии (генератор, методики, чат). Запросы без нового поля (эмбеддинги и пр.) не трогаются.
/// </remarks>
internal sealed class LegacyMaxTokensPolicy : PipelinePolicy
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

    private static void Rewrite(PipelineMessage message)
    {
        if (message.Request?.Content is not { } content)
        {
            return;
        }

        using var buffer = new MemoryStream();
        content.WriteTo(buffer);
        if (RewriteBody(BinaryData.FromBytes(buffer.ToArray())) is { } rewritten)
        {
            message.Request.Content = BinaryContent.Create(rewritten);
        }
    }

    /// <summary>
    /// Дублирует <c>max_completion_tokens</c> старым полем <c>max_tokens</c>;
    /// <see langword="null"/> — переписывать нечего (поля нет, уже есть старое или тело не JSON-объект).
    /// </summary>
    internal static BinaryData? RewriteBody(BinaryData body)
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

        if (root is not JsonObject json
            || json["max_completion_tokens"] is not JsonValue limit
            || json.ContainsKey("max_tokens"))
        {
            return null;
        }

        json["max_tokens"] = limit.DeepClone();
        return BinaryData.FromString(json.ToJsonString());
    }
}
