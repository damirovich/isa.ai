namespace ISC.AI.Abstractions.Conversations;

/// <summary>
/// Хранилище диалогов чата и их сообщений (сохранение истории общения). Доменно-нейтрально: любой профиль
/// переиспользует. ВСЕ операции разграничены по владельцу (<c>subjectId</c>) — диалог содержит ДСП, поэтому
/// пользователь работает ТОЛЬКО со своими диалогами (fail-closed: чужой/несуществующий диалог = «не найден»).
/// </summary>
/// <remarks>
/// ОБЛАСТИ И КЛЮЧИ УНИЧТОЖЕНИЯ (ADR-0052). Диалог пакета чата помнит область профиля (<c>area</c> — строка, смысл
/// которой знает только профиль; <see langword="null"/> — область по умолчанию) и ключи уничтожения (<c>scope keys</c>)
/// материалов, на которые опирались ответы. Ядро ключи не толкует: профиль уничтожает свой объект — и вызывает
/// <see cref="PurgeScopeAsync"/> с его ключом (ТБ-064). Проверку, видна ли область субъекту, ведёт пакет чата через
/// порт профиля — хранилище отвечает только за владельца и допуск к сообщениям.
/// </remarks>
public interface IConversationStore
{
    /// <summary>Создаёт новый диалог владельца <paramref name="subjectId"/> и возвращает его идентификатор.</summary>
    Task<int> CreateAsync(int subjectId, string title, CancellationToken cancellationToken = default);

    /// <summary>
    /// Создаёт диалог владельца в области профиля (ADR-0052) с начальным грифом и ключами уничтожения области.
    /// </summary>
    /// <param name="subjectId">Владелец.</param>
    /// <param name="title">Заголовок (обычно — начало первого вопроса).</param>
    /// <param name="area">Область профиля; <see langword="null"/> — область по умолчанию.</param>
    /// <param name="classification">Начальный гриф диалога (например, гриф дела, по которому он ведётся).</param>
    /// <param name="scopeKeys">Ключи уничтожения области (например, дело).</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    Task<int> CreateAsync(
        int subjectId,
        string title,
        string? area,
        short classification,
        IReadOnlyCollection<string> scopeKeys,
        CancellationToken cancellationToken = default);

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

    /// <summary>
    /// Добавляет сообщение с источниками и итогом проверки ссылок (ADR-0052): гриф диалога поднимается до грифа
    /// сообщения, ключи уничтожения сообщения добавляются к ключам диалога. <see langword="false"/> — диалог субъекту
    /// не принадлежит.
    /// </summary>
    Task<bool> AppendMessageAsync(
        int conversationId, int subjectId, ConversationMessageDraft message, CancellationToken cancellationToken = default);

    /// <summary>Возвращает историю диалога (по возрастанию времени) — только для владельца; иначе пусто.</summary>
    Task<IReadOnlyList<ChatTurn>> GetHistoryAsync(
        int conversationId, int subjectId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Диалог владельца с сообщениями не выше <paramref name="maxClassification"/> (текущий допуск субъекта, ТБ-020) и
    /// числом скрытых; <see langword="null"/> — нет или чужой.
    /// </summary>
    Task<ConversationThread?> GetAsync(
        int conversationId, int subjectId, short maxClassification, CancellationToken cancellationToken = default);

    /// <summary>Карточка диалога владельца (с областью) без сообщений; <see langword="null"/> — нет или чужой.</summary>
    Task<ConversationSummary?> FindAsync(int conversationId, int subjectId, CancellationToken cancellationToken = default);

    /// <summary>Список диалогов владельца (новые сверху) для боковой панели.</summary>
    Task<IReadOnlyList<ConversationSummary>> ListAsync(int subjectId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Диалоги владельца в области профиля (новые сверху); <paramref name="area"/> = <see langword="null"/> — область по
    /// умолчанию (в том числе диалоги, заведённые до появления областей).
    /// </summary>
    Task<IReadOnlyList<ConversationSummary>> ListInAreaAsync(
        int subjectId, string? area, CancellationToken cancellationToken = default);

    /// <summary>Переименовывает диалог владельца. <see langword="false"/>, если диалог субъекту не принадлежит.</summary>
    Task<bool> RenameAsync(
        int conversationId, int subjectId, string title, CancellationToken cancellationToken = default);

    /// <summary>Мягко удаляет диалог владельца (скрывает из списка). <see langword="false"/>, если не его.</summary>
    Task<bool> DeleteAsync(int conversationId, int subjectId, CancellationToken cancellationToken = default);

    /// <summary>Сколько диалогов (любых владельцев, в том числе скрытых) несут ключ уничтожения — для акта.</summary>
    Task<int> CountScopeAsync(string scopeKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// ФИЗИЧЕСКИ уничтожает все диалоги с ключом <paramref name="scopeKey"/> — любых владельцев, в том числе скрытые, — с
    /// сообщениями (ТБ-064). Журнал не пишет: акт уничтожения пишет вызывающий ДО вызова. Возвращает число диалогов.
    /// </summary>
    Task<int> PurgeScopeAsync(string scopeKey, CancellationToken cancellationToken = default);
}
