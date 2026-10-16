using ISC.AI.Abstractions.Enums;

namespace ISC.AI.Abstractions.Conversations;

/// <summary>
/// Диалог с сообщениями для показа (ADR-0052). Сообщения — только в пределах текущего допуска субъекта; сколько скрыто
/// выше допуска — <see cref="HiddenMessages"/> (допуск могли понизить после ответа, ТБ-020).
/// </summary>
/// <param name="Id">Диалог.</param>
/// <param name="Area">Область профиля, в которой ведётся диалог; <see langword="null"/> — область по умолчанию.</param>
/// <param name="Title">Заголовок.</param>
/// <param name="Classification">Гриф диалога — максимум грифов его сообщений.</param>
/// <param name="Messages">Сообщения не выше допуска, по порядку.</param>
/// <param name="HiddenMessages">Сколько сообщений скрыто: их гриф выше текущего допуска.</param>
public sealed record ConversationThread(
    int Id,
    string? Area,
    string Title,
    short Classification,
    IReadOnlyList<ConversationMessage> Messages,
    int HiddenMessages);

/// <summary>Сообщение диалога для показа.</summary>
/// <param name="Id">Сообщение.</param>
/// <param name="Role">Автор.</param>
/// <param name="Content">Текст.</param>
/// <param name="Classification">Гриф сообщения.</param>
/// <param name="CreatedAt">Когда (UTC).</param>
/// <param name="SourcesJson">Источники ответа (jsonb; формат знает пакет чата); у вопроса — <see langword="null"/>.</param>
/// <param name="CheckJson">Итог проверки ссылок ответа (jsonb; формат знает пакет чата); у вопроса — <see langword="null"/>.</param>
public sealed record ConversationMessage(
    int Id,
    ConversationMessageRole Role,
    string Content,
    short Classification,
    DateTime CreatedAt,
    string? SourcesJson,
    string? CheckJson);

/// <summary>Новое сообщение диалога (ADR-0052).</summary>
/// <param name="Role">Автор.</param>
/// <param name="Content">Текст.</param>
/// <param name="Classification">Гриф: гриф диалога поднимается до него.</param>
/// <param name="GroundingJson">Вердикт грунтовки ответа (jsonb) — для разбора.</param>
/// <param name="SourcesJson">Источники ответа (jsonb).</param>
/// <param name="CheckJson">Итог проверки ссылок ответа (jsonb).</param>
/// <param name="ScopeKeys">
/// Ключи уничтожения материалов, на которые опирался ответ (например, дело): добавляются к ключам диалога, и диалог
/// уничтожается вместе с любым из этих материалов (<see cref="IConversationStore.PurgeScopeAsync"/>, ТБ-064).
/// </param>
public sealed record ConversationMessageDraft(
    ConversationMessageRole Role,
    string Content,
    short Classification,
    string? GroundingJson = null,
    string? SourcesJson = null,
    string? CheckJson = null,
    IReadOnlyCollection<string>? ScopeKeys = null);
