using System.Text.Json;
using ISC.AI.Abstractions.Conversations;
using ISC.AI.Abstractions.Enums;
using ISC.AI.Abstractions.Retrieval;
using ISC.AI.Modules.Chat.Domain.Services;

namespace ISC.AI.Modules.Chat.Application.Features;

/// <summary>
/// Источник ответа: фрагмент корпуса, поданный модели под номером <see cref="Number"/> («[Фрагмент N]»), с подписью от
/// профиля (ADR-0052). Хранится в сообщении (<c>core.conversation_message.sources_json</c>).
/// </summary>
/// <param name="Number">Номер фрагмента в запросе к модели (с 1) — по нему ответ ссылается на источник.</param>
/// <param name="Kind">Вид словами: «документ», «расшифровка».</param>
/// <param name="Label">Подпись источника.</param>
/// <param name="Href">Что открыть; <see langword="null"/> — без ссылки.</param>
/// <param name="ChunkId">Фрагмент корпуса ядра (<c>core.chunk.id</c>) — для разбора по журналу.</param>
/// <param name="Classification">Гриф фрагмента.</param>
/// <param name="Excerpt">Начало текста фрагмента (не длиннее <see cref="ChatLimits.MaxExcerptLength"/>).</param>
/// <param name="ScopeKey">Ключ уничтожения материала (например, дело).</param>
public sealed record ChatSource(
    int Number, string Kind, string Label, string? Href, int ChunkId, short Classification, string Excerpt, string? ScopeKey = null)
{
    /// <summary>Источник по фрагменту с подписью профиля; <see langword="null"/> — фрагмент не из материалов области.</summary>
    public static ChatSource? From(int number, RetrievedChunk fragment, ChatScope scope)
    {
        ArgumentNullException.ThrowIfNull(fragment);
        ArgumentNullException.ThrowIfNull(scope);
        return scope.Describe(fragment) is { } source
            ? new ChatSource(number, source.Kind, source.Label, source.Href, fragment.ChunkId, fragment.Classification,
                Shorten(fragment.Text), source.ScopeKey)
            : null;
    }

    /// <summary>Начало текста фрагмента в одну строку, не длиннее <see cref="ChatLimits.MaxExcerptLength"/>.</summary>
    public static string Shorten(string? text)
    {
        var value = string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return value.Length <= ChatLimits.MaxExcerptLength ? value : value[..(ChatLimits.MaxExcerptLength - 1)] + "…";
    }
}

/// <summary>Итог проверки ссылок ответа (ТБ-040, ADR-0051/0052).</summary>
/// <param name="Unverified">Ссылки ответа (номера, статьи), которых нет в поданных фрагментах, — «по памяти» модели.</param>
/// <param name="UnknownFragments">Номера «[Фрагмент N]», которых модели не подавали.</param>
public sealed record ChatCheck(IReadOnlyList<string> Unverified, IReadOnlyList<int> UnknownFragments)
{
    /// <summary>Все ссылки ответа подтверждены поданными фрагментами.</summary>
    public bool AllConfirmed => Unverified.Count == 0 && UnknownFragments.Count == 0;
}

/// <summary>Готовый ответ: сохранён в диалоге вместе с источниками и итогом проверки ссылок.</summary>
/// <param name="Text">Текст ответа.</param>
/// <param name="Classification">Гриф ответа.</param>
/// <param name="Sources">Фрагменты, поданные модели, с подписями профиля; в свободном режиме — пусто.</param>
/// <param name="Check">Итог проверки ссылок; в свободном режиме — <see langword="null"/> (не сверялось).</param>
public sealed record ChatAnswer(string Text, short Classification, IReadOnlyList<ChatSource> Sources, ChatCheck? Check);

/// <summary>
/// Обновление потока ответа. Первое — <see cref="ConversationId"/> (диалог, куда записан вопрос), затем черновик кусками
/// (<see cref="TextDelta"/>), последнее — <see cref="Final"/> или <see cref="Error"/>.
/// </summary>
/// <param name="ConversationId">Диалог (в первом обновлении).</param>
/// <param name="TextDelta">Кусок черновика — НЕпроверенный текст, пока не пришёл итог.</param>
/// <param name="Final">Итог: проверенный ответ с источниками.</param>
/// <param name="Error">Отказ или сбой — текст для сотрудника.</param>
public sealed record ChatUpdate(int? ConversationId = null, string? TextDelta = null, ChatAnswer? Final = null, string? Error = null);

/// <summary>Чат области: область, свои диалоги, готовность материалов и подсказки.</summary>
/// <param name="Area">Область.</param>
/// <param name="Conversations">Свои диалоги области, новые сверху.</param>
/// <param name="Coverage">Строка «что видит чат»; <see langword="null"/> — не показывать.</param>
/// <param name="CoverageHint">Пояснение к строке готовности.</param>
/// <param name="Suggestions">Подсказки области.</param>
public sealed record ChatOverview(
    ChatArea Area, IReadOnlyList<ConversationSummary> Conversations, string? Coverage, string? CoverageHint, ChatSuggestions Suggestions);

/// <summary>Сообщение диалога для показа.</summary>
/// <param name="Id">Сообщение.</param>
/// <param name="Role">Автор.</param>
/// <param name="Text">Текст.</param>
/// <param name="Classification">Гриф.</param>
/// <param name="CreatedAt">Когда (UTC).</param>
/// <param name="Sources">Источники ответа (у вопроса и свободного ответа — пусто).</param>
/// <param name="Check">Итог проверки ссылок ответа (у вопроса и свободного ответа — <see langword="null"/>).</param>
public sealed record ChatMessageView(
    int Id, ConversationMessageRole Role, string Text, short Classification, DateTime CreatedAt, IReadOnlyList<ChatSource> Sources, ChatCheck? Check);

/// <summary>Свой диалог с сообщениями в пределах текущего допуска.</summary>
/// <param name="Id">Диалог.</param>
/// <param name="AreaKey">Область диалога.</param>
/// <param name="Title">Заголовок.</param>
/// <param name="Classification">Гриф диалога.</param>
/// <param name="Messages">Сообщения не выше допуска, по порядку.</param>
/// <param name="HiddenMessages">Сколько сообщений скрыто: их гриф выше текущего допуска (ТБ-020).</param>
public sealed record ChatThreadView(
    int Id, string? AreaKey, string Title, short Classification, IReadOnlyList<ChatMessageView> Messages, int HiddenMessages);

/// <summary>Найденный по смыслу фрагмент материала.</summary>
/// <param name="Source">Источник (вид, подпись, что открыть).</param>
/// <param name="Text">Текст фрагмента.</param>
/// <param name="Similarity">Близость к запросу, 0…1 (1 — совпадение по смыслу).</param>
public sealed record ChatSenseHit(ChatSource Source, string Text, double Similarity);

/// <summary>Итог поиска по смыслу.</summary>
/// <param name="Hits">Фрагменты, ближайшие первыми.</param>
/// <param name="Materials">Материалов в области поиска; <see langword="null"/> — весь доступный корпус.</param>
public sealed record ChatSenseResult(IReadOnlyList<ChatSenseHit> Hits, int? Materials);

/// <summary>
/// Хранение источников и проверки в jsonb сообщения. Чтение терпит старые и чужие строки: диалог, заведённый до пакета
/// (у «ИнспектораAI» источники не хранились), показывается без источников, а не падает.
/// </summary>
public static class ChatJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>Источники в jsonb; пусто — <see langword="null"/>.</summary>
    public static string? Write(IReadOnlyList<ChatSource> sources) =>
        sources is { Count: > 0 } ? JsonSerializer.Serialize(sources, Options) : null;

    /// <summary>Проверка в jsonb.</summary>
    public static string? Write(ChatCheck? check) => check is null ? null : JsonSerializer.Serialize(check, Options);

    /// <summary>Вердикт грунтовки ядра в jsonb — для разбора (как у прежнего чата ядра).</summary>
    public static string Write(Abstractions.Grounding.GroundingResult grounding) => JsonSerializer.Serialize(grounding, Options);

    /// <summary>Источники из jsonb; нечитаемое — пусто.</summary>
    public static IReadOnlyList<ChatSource> ReadSources(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<ChatSource>>(json, Options) is { } list
                ? list.Where(s => s is { Kind: not null, Label: not null }).ToList()
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Проверка из jsonb; нечитаемое — <see langword="null"/>.</summary>
    public static ChatCheck? ReadCheck(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ChatCheck>(json, Options) is { } check
                ? new ChatCheck(check.Unverified ?? [], check.UnknownFragments ?? [])
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
