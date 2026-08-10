using ISC.AI.Abstractions.Entities;

namespace ISC.AI.Modules.DocFlow.Domain.Entities;

/// <summary>
/// Продление срока назначения (ТЗ СКИД §4.6): на уровне КОНКРЕТНОГО назначения, с обязательным
/// основанием; количество продлений не ограничено; после продления назначение возвращается «В работу».
/// </summary>
public class DeadlineExtension : AuditableEntity
{
    /// <summary>Назначение (FK внутри схемы).</summary>
    public int AssignmentId { get; set; }

    /// <summary>Прежний срок.</summary>
    public DateOnly OldDeadline { get; set; }

    /// <summary>Новый срок.</summary>
    public DateOnly NewDeadline { get; set; }

    /// <summary>Основание продления (обязательное текстовое поле, §4.6).</summary>
    public required string Reason { get; set; }

    /// <summary>Инициатор — слабая ссылка на <c>core.app_user</c> (ТО-инф-06).</summary>
    public int InitiatedByUserId { get; set; }
}
