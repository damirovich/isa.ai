namespace ISC.AI.Modules.DocFlow.Domain.Enums;

/// <summary>
/// Вид уведомления (разд. 5 ТЗ СКИД). Доставка — ТОЛЬКО в интерфейсе, без email (согласуется с air-gap).
/// </summary>
/// <remarks>
/// Перенесены виды, для которых в модуле УЖЕ есть порождающее событие. Виды СКИД
/// <c>Reassigned</c>/<c>InspectorReassigned</c> НЕ переносятся: переназначение исполнителя (§4.7) ещё
/// не перенесено (отложено в 3.x) — значение перечисления без порождающего кода было бы мёртвым.
/// </remarks>
public enum NotificationType
{
    /// <summary>Срок наступает через горизонт уведомлений (<c>DocFlow:NotificationHorizonDays</c>).</summary>
    DeadlineApproaching = 1,

    /// <summary>Срок наступает сегодня.</summary>
    DeadlineToday = 2,

    /// <summary>Назначение переведено в «Просрочено» системой (§4.2).</summary>
    Overdue = 3,

    /// <summary>Статус назначения изменён (§4.2).</summary>
    StatusChanged = 4,

    /// <summary>Создано назначение по документу (§4.1).</summary>
    Assigned = 5,

    /// <summary>Упоминание в комментарии (§4.8).</summary>
    MentionedInComment = 6,

    /// <summary>Добавлен комментарий к документу (§4.8).</summary>
    CommentAdded = 7,

    /// <summary>Срок назначения продлён (§4.6).</summary>
    DeadlineExtended = 9,
}
