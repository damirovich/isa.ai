using ISC.AI.Abstractions.Entities;

namespace ISC.AI.Modules.DocFlow.Domain.Entities;

/// <summary>Файл, прикреплённый к переходу статуса назначения (ТЗ СКИД §4.2/§4.8 — на каждом переходе).</summary>
public class StatusHistoryFile : AuditableEntity
{
    /// <summary>Переход статуса (FK внутри схемы).</summary>
    public int StatusHistoryId { get; set; }

    /// <summary>Навигация к переходу (для записи файла до присвоения идентификатора перехода).</summary>
    public AssignmentStatusHistory? StatusHistory { get; set; }

    /// <summary>Исходное имя файла.</summary>
    public required string FileName { get; set; }

    /// <summary>Имя в защищённом хранилище.</summary>
    public required string StoredFileName { get; set; }

    /// <summary>MIME-тип.</summary>
    public required string ContentType { get; set; }

    /// <summary>Размер, байт.</summary>
    public long FileSize { get; set; }

    /// <summary>Кто загрузил — слабая ссылка на <c>core.app_user</c> (ТО-инф-06).</summary>
    public int UploadedByUserId { get; set; }
}
