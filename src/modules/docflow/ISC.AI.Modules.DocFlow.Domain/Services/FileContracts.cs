using ISC.AI.Modules.DocFlow.Domain.Enums;

namespace ISC.AI.Modules.DocFlow.Domain.Services;

// Контракты файлов документа (§3.3): версионируемый файл и сопутствующее вложение.

/// <summary>
/// Файл документа в карточке (§3.3): версия, актуальность, язык. <see cref="StoredFileName"/> —
/// для построения ссылки просмотра/скачивания (этап 4.3).
/// </summary>
public sealed record DocumentFileItem(
    int Id, string FileName, DocumentLanguage Language, int Version, bool IsLatest,
    long FileSize, DateTime UploadedAt, string StoredFileName);

/// <summary>Сопутствующий файл в карточке.</summary>
public sealed record AttachmentItem(int Id, string FileName, long FileSize, DateTime UploadedAt, string StoredFileName);
