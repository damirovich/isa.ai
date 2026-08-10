using ISC.AI.Abstractions.Entities;

namespace ISC.AI.Modules.DocFlow.Domain.Entities;

/// <summary>
/// Смена исполнителя назначения (ТЗ СКИД §4.7): кто, когда, с кого на кого и почему.
/// </summary>
/// <remarks>
/// В СКИД переназначение НЕ фиксировалось в истории назначения вовсе — след оставался только в
/// журнале аудита, доступном администратору. Для пользователя это выглядело так: исполнитель у
/// поручения молча поменялся, и понять когда и по чьему решению было нельзя. Отдельная таблица (а не
/// запись в <see cref="AssignmentStatusHistory"/>) — потому что смена исполнителя НЕ является сменой
/// статуса: подмешивать её туда значило бы врать про переход, которого не было. В ленте событий
/// (§4.8) оба источника сводятся вместе.
/// </remarks>
public class AssignmentReassignment : AuditableEntity
{
    /// <summary>Назначение (FK внутри схемы).</summary>
    public int AssignmentId { get; set; }

    /// <summary>Навигация к назначению.</summary>
    public DocumentAssignment? Assignment { get; set; }

    /// <summary>Прежний исполнитель (<see langword="null"/> — назначение было «на подразделение», без лица).</summary>
    public int? FromUserId { get; set; }

    /// <summary>Новый исполнитель — слабая ссылка на <c>core.app_user</c> (ТО-инф-06).</summary>
    public int ToUserId { get; set; }

    /// <summary>Кто переназначил — слабая ссылка на <c>core.app_user</c>.</summary>
    public int? ChangedByUserId { get; set; }

    /// <summary>Момент переназначения (UTC).</summary>
    public DateTime ChangedAt { get; set; }

    /// <summary>
    /// Основание. НЕобязательно — в СКИД его не запрашивали вовсе; здесь поле есть, но не требуется,
    /// чтобы не делать сценарий строже оригинала (у продления срока основание, наоборот, обязательно).
    /// </summary>
    public string? Reason { get; set; }
}
