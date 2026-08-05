using ISC.AI.Abstractions.Entities;
using ISC.AI.Modules.DocFlow.Domain.Enums;

namespace ISC.AI.Modules.DocFlow.Domain.Entities;

/// <summary>
/// Версионируемый файл документа (ТЗ СКИД §3.3): при замене создаётся новая версия, прежняя
/// сохраняется; актуальную помечает <see cref="IsLatest"/>. Файлы просматриваются в интерфейсе
/// без скачивания (§3.3 — согласуется с режимом ДСП).
/// </summary>
public class DocumentFile : AuditableEntity
{
    /// <summary>Документ (FK внутри схемы).</summary>
    public int DocumentId { get; set; }

    /// <summary>Язык содержимого (двуязычие рус/кырг, §7.1).</summary>
    public DocumentLanguage Language { get; set; }

    /// <summary>Исходное имя файла.</summary>
    public required string FileName { get; set; }

    /// <summary>Имя в защищённом хранилище (генерируется, не совпадает с исходным).</summary>
    public required string StoredFileName { get; set; }

    /// <summary>MIME-тип.</summary>
    public required string ContentType { get; set; }

    /// <summary>Размер, байт.</summary>
    public long FileSize { get; set; }

    /// <summary>Номер версии (растёт при каждой замене, §3.3).</summary>
    public int Version { get; set; }

    /// <summary>Актуальная ли версия.</summary>
    public bool IsLatest { get; set; }

    /// <summary>Имя PDF-копии в хранилище (DOCX→PDF конвертером; для просмотра), если создана.</summary>
    public string? PdfCopyStoredFileName { get; set; }

    /// <summary>Кто загрузил — слабая ссылка на <c>core.app_user</c> (ТО-инф-06).</summary>
    public int UploadedByUserId { get; set; }
}
