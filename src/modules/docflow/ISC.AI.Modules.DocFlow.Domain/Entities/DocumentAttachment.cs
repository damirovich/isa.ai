using ISC.AI.Abstractions.Entities;

namespace ISC.AI.Modules.DocFlow.Domain.Entities;

/// <summary>Сопутствующий файл документа (без версионирования — в отличие от <see cref="DocumentFile"/>).</summary>
public class DocumentAttachment : AuditableEntity
{
    /// <summary>Документ (FK внутри схемы).</summary>
    public int DocumentId { get; set; }

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
