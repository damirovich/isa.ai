using ISC.AI.Abstractions.Entities;
using ISC.AI.Modules.DocFlow.Domain.Enums;

namespace ISC.AI.Modules.DocFlow.Domain.Entities;

/// <summary>
/// Назначение — независимая единица исполнения документа подразделением (ТЗ СКИД §4.1):
/// свой ответственный, свой срок, свой статус и своя история. Документ группы «Исполнение»
/// может иметь несколько назначений одновременно.
/// </summary>
public class DocumentAssignment : AuditableEntity
{
    /// <summary>Документ (FK внутри схемы <c>docflow</c>; удаление документа удаляет назначения).</summary>
    public int DocumentId { get; set; }

    /// <summary>Навигация к документу.</summary>
    public Document? Document { get; set; }

    /// <summary>
    /// Ответственное подразделение. Тот же словарь идентификаторов, что решётка доступа ядра
    /// (<c>core.clearance</c> / <c>AccessContext.AllowedDivisions</c>); слабая ссылка без FK через
    /// границу схем (ТО-инф-06). Справочник подразделений ведёт профиль (см. вопрос 3 Э4-35).
    /// </summary>
    public int DivisionId { get; set; }

    /// <summary>Ответственное лицо (Исполнитель) — слабая ссылка на <c>core.app_user</c> (ТО-инф-06).</summary>
    public int? AssigneeUserId { get; set; }

    /// <summary>Статус исполнения (цепочка §4.2, независим от других назначений).</summary>
    public AssignmentStatus Status { get; set; } = AssignmentStatus.Registered;

    /// <summary>Срок исполнения ДАННОГО назначения (у разных назначений может отличаться, §4.1).</summary>
    public DateOnly? Deadline { get; set; }

    /// <summary>Срок проставлен чекбоксом «единый срок для всех подразделений» (§4.1).</summary>
    public bool UseCommonDeadline { get; set; }

    /// <summary>Контролёр (статус «Контроль», §4.2) — слабая ссылка на <c>core.app_user</c>.</summary>
    public int? ControllerUserId { get; set; }

    /// <summary>Токен оптимистической блокировки — системная колонка PostgreSQL <c>xmin</c> (как в СКИД).</summary>
    public uint Xmin { get; set; }
}
