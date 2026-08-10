using ISC.AI.Abstractions.Entities;
using ISC.AI.Modules.DocFlow.Domain.Enums;

namespace ISC.AI.Modules.DocFlow.Domain.Entities;

/// <summary>
/// Переход статуса назначения (ТЗ СКИД §4.8): кто, когда, из какого статуса в какой, комментарий.
/// Файлы перехода — <see cref="StatusHistoryFile"/>. История доступна в карточке документа.
/// </summary>
public class AssignmentStatusHistory : AuditableEntity
{
    /// <summary>Назначение (FK внутри схемы).</summary>
    public int AssignmentId { get; set; }

    /// <summary>Навигация к назначению (для записи истории до присвоения идентификатора).</summary>
    public DocumentAssignment? Assignment { get; set; }

    /// <summary>Из какого статуса (<see langword="null"/> — создание назначения).</summary>
    public AssignmentStatus? FromStatus { get; set; }

    /// <summary>В какой статус.</summary>
    public AssignmentStatus ToStatus { get; set; }

    /// <summary>Инициатор перехода — слабая ссылка на <c>core.app_user</c> (<see langword="null"/> — система, напр. «Просрочено»).</summary>
    public int? ChangedByUserId { get; set; }

    /// <summary>Момент перехода (UTC).</summary>
    public DateTime ChangedAt { get; set; }

    /// <summary>Текстовый комментарий к переходу (§4.2 — можно оставить на каждом переходе).</summary>
    public string? Comment { get; set; }
}
