namespace ISC.AI.Modules.DocFlow.Domain.Enums;

/// <summary>
/// Вид уведомления (разд. 5 ТЗ СКИД). Доставка — ТОЛЬКО в интерфейсе, без email (согласуется с air-gap).
/// </summary>
/// <remarks>
/// Заводятся виды, для которых в модуле ЕСТЬ порождающее событие. Вид СКИД <c>InspectorReassigned</c>
/// долго отсутствовал именно поэтому: разбор исходника (этап 3.2) показал, что он рождается НЕ при
/// переназначении исполнителя, а при РЕДАКТИРОВАНИИ документа, когда меняют ответственного инспектора.
/// С появлением правки карточки событие появилось — см. <see cref="NotificationType.InspectorChanged"/>.
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

    /// <summary>Исполнитель назначения переназначен (§4.7).</summary>
    Reassigned = 8,

    /// <summary>Срок назначения продлён (§4.6).</summary>
    DeadlineExtended = 9,

    /// <summary>
    /// Сменился ответственный ИНСПЕКТОР документа (при правке карточки, §3.2).
    /// </summary>
    /// <remarks>
    /// Это и есть вид СКИД <c>InspectorReassigned</c>, о котором сказано выше: теперь у него появилось
    /// порождающее событие — редактирование документа. НЕ путать с <see cref="Reassigned"/>: там
    /// меняется ИСПОЛНИТЕЛЬ одного назначения (§4.7), здесь — инспектор всего документа.
    /// </remarks>
    InspectorChanged = 10,
}
