namespace ISC.AI.Abstractions.Conversations;

/// <summary>Краткая карточка диалога для списка (боковая панель чата).</summary>
/// <param name="Id">Идентификатор диалога.</param>
/// <param name="Title">Заголовок (обычно — начало первого запроса).</param>
/// <param name="Classification">Максимальный гриф содержимого диалога (для маркировки).</param>
/// <param name="UpdatedAt">Время последнего сообщения (для сортировки списка).</param>
public sealed record ConversationSummary(int Id, string Title, short Classification, DateTime UpdatedAt);
