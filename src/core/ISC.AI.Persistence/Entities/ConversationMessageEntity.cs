using ISC.AI.Abstractions.Entities;
using ISC.AI.Abstractions.Enums;

namespace ISC.AI.Persistence.Entities;

/// <summary>
/// Сообщение диалога — реплика пользователя или ответ ассистента. Схема <c>core</c>. Несёт гриф (для ответа —
/// унаследованный максимум грифов использованных фрагментов). Для ответа ассистента в <see cref="GroundingJson"/>
/// сохраняется итог грунтовки (подтверждённые/непроверенные ссылки) — для отображения и разбора.
/// </summary>
public class ConversationMessageEntity : AuditableEntity
{
    /// <summary>Диалог-владелец (FK в пределах схемы <c>core</c>).</summary>
    public int ConversationId { get; set; }

    /// <summary>Автор сообщения.</summary>
    public ConversationMessageRole Role { get; set; }

    /// <summary>Текст сообщения.</summary>
    public required string Content { get; set; }

    /// <summary>Гриф сообщения (для ответа — максимум грифов использованных фрагментов, ТБ-032/033).</summary>
    public short Classification { get; set; }

    /// <summary>Итог грунтовки ответа (jsonb: список ссылок и их статусы). <see langword="null"/> для реплики пользователя.</summary>
    public string? GroundingJson { get; set; }

    /// <summary>Навигация к диалогу-владельцу.</summary>
    public ConversationEntity? Conversation { get; set; }
}
