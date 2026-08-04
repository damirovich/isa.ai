namespace ISC.AI.Abstractions.Enums;

/// <summary>Автор сообщения в диалоге чата.</summary>
public enum ConversationMessageRole
{
    /// <summary>Сообщение пользователя (запрос).</summary>
    User = 0,

    /// <summary>Ответ ассистента (грунтованная генерация).</summary>
    Assistant = 1,
}
