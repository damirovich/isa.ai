namespace ISC.AI.Abstractions.Conversations;

/// <summary>
/// Хранилище диалогов чата и их сообщений (сохранение истории общения). Доменно-нейтрально: любой профиль
/// переиспользует. ВСЕ операции разграничены по владельцу (<c>subjectId</c>) — диалог содержит ДСП, поэтому
/// пользователь работает ТОЛЬКО со своими диалогами (fail-closed: чужой/несуществующий диалог = «не найден»).
/// </summary>
public interface IConversationStore
{
    /// <summary>Создаёт новый диалог владельца <paramref name="subjectId"/> и возвращает его идентификатор.</summary>
    Task<int> CreateAsync(int subjectId, string title, CancellationToken cancellationToken = default);

    /// <summary>
    /// Добавляет сообщение в диалог (если он принадлежит субъекту), повышает гриф диалога до
    /// <paramref name="classification"/> и обновляет время активности. Возвращает <see langword="false"/>,
    /// если диалог субъекту не принадлежит.
    /// </summary>
    Task<bool> AppendMessageAsync(
        int conversationId,
        int subjectId,
        Enums.ConversationMessageRole role,
        string content,
        short classification,
        string? groundingJson,
        CancellationToken cancellationToken = default);

    /// <summary>Возвращает историю диалога (по возрастанию времени) — только для владельца; иначе пусто.</summary>
    Task<IReadOnlyList<ChatTurn>> GetHistoryAsync(
        int conversationId, int subjectId, CancellationToken cancellationToken = default);

    /// <summary>Список диалогов владельца (новые сверху) для боковой панели.</summary>
    Task<IReadOnlyList<ConversationSummary>> ListAsync(int subjectId, CancellationToken cancellationToken = default);

    /// <summary>Переименовывает диалог владельца. <see langword="false"/>, если диалог субъекту не принадлежит.</summary>
    Task<bool> RenameAsync(
        int conversationId, int subjectId, string title, CancellationToken cancellationToken = default);

    /// <summary>Мягко удаляет диалог владельца (скрывает из списка). <see langword="false"/>, если не его.</summary>
    Task<bool> DeleteAsync(int conversationId, int subjectId, CancellationToken cancellationToken = default);
}
