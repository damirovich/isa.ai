using ISC.AI.Abstractions.Entities;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Domain.Model;

namespace ISC.AI.Modules.Media.Data.Entities;

/// <summary>
/// Строка задания импорта (<c>media.import_item</c>, ADR-0041) — один файл архива: путь, дело, подтверждённые гриф и
/// подразделение, состояние приёма, причина отказа, хеш до и после переноса, носитель и подача в обработку.
/// </summary>
/// <remarks>
/// Режимные поля строки — подтверждённые оператором гриф и подразделение ДЕЛА (ТБ-024/070): по ним строка читается
/// под решёткой (ТБ-020), потому что путь файла и номер дела сами могут быть сведениями ограниченного доступа. Дело —
/// слабая ссылка по значению на схему профиля (ТО-инф-08). Носитель — FK внутри схемы с обнулением: гарантированное
/// удаление носителя (ТБ-075) не должно упираться в историю импорта, а строка остаётся свидетельством, что файл
/// принимался.
/// </remarks>
public class MediaImportItem : BaseEntity, IClassified
{
    /// <summary>Задание.</summary>
    public int JobId { get; set; }

    /// <summary>Номер строки описи.</summary>
    public int RowNumber { get; set; }

    /// <summary>Путь файла относительно папки задания (разделитель «/»).</summary>
    public required string RelativePath { get; set; }

    /// <summary>Вид файла по расширению: носитель или документ (ADR-0042).</summary>
    public MediaImportFileKind Kind { get; set; }

    /// <summary>MIME-тип по расширению.</summary>
    public required string ContentType { get; set; }

    /// <summary>Размер на проверке, байт.</summary>
    public long ByteSize { get; set; }

    /// <summary>Дело (значение из схемы профиля).</summary>
    public int CaseId { get; set; }

    /// <summary>Номер дела — для отчёта.</summary>
    public required string CaseNumber { get; set; }

    /// <summary>Подтверждённый гриф дела (ТБ-024). NOT NULL.</summary>
    public short Classification { get; set; }

    /// <summary>Подтверждённое подразделение дела. NOT NULL.</summary>
    public int DivisionId { get; set; }

    /// <summary>Дата съёмки из описи; <see langword="null"/> — из метаданных файла при приёме.</summary>
    public DateTimeOffset? CapturedAt { get; set; }

    /// <summary>В описи была только дата (взято 00:00 местного времени).</summary>
    public bool CapturedDateOnly { get; set; }

    /// <summary>Источник.</summary>
    public string? Source { get; set; }

    /// <summary>Место (на привязке к делу).</summary>
    public string? Place { get; set; }

    /// <summary>SHA-256 из описи — «до переноса».</summary>
    public string? ExpectedSha256 { get; set; }

    /// <summary>Состояние приёма.</summary>
    public MediaImportItemStatus Status { get; set; } = MediaImportItemStatus.Pending;

    /// <summary>Причина отказа.</summary>
    public string? Reason { get; set; }

    /// <summary>Носитель — принятый или существующий дубликат.</summary>
    public int? AssetId { get; set; }

    /// <summary>SHA-256, посчитанный при приёме, — «после переноса».</summary>
    public string? ContentSha256 { get; set; }

    /// <summary>Когда разобрана строка (UTC).</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>Подача носителя в обработку.</summary>
    public MediaImportProcessingState Processing { get; set; } = MediaImportProcessingState.None;

    /// <summary>Тип документа документооборота (строки-документы, ADR-0042).</summary>
    public int? DocumentTypeId { get; set; }

    /// <summary>Регистрационный номер документа из описи.</summary>
    public string? DocumentRegNumber { get; set; }

    /// <summary>Дата документа.</summary>
    public DateOnly? DocumentRegDate { get; set; }

    /// <summary>Даты не было в описи — взята дата изменения файла.</summary>
    public bool DocumentRegDateFromFile { get; set; }

    /// <summary>Краткое содержание документа.</summary>
    public string? DocumentTitle { get; set; }

    /// <summary>
    /// Документ документооборота, созданный строкой (ADR-0042) — слабая ссылка на схему документооборота (ТО-инф-06).
    /// Пишется сразу после создания документа, до загрузки файла: прерванный приём продолжает этот документ.
    /// </summary>
    public int? DocumentId { get; set; }

    /// <summary>Фоновая задача индексации лиц (носитель) или индексации текста (документ), поданная заданием.</summary>
    public Guid? IndexTaskId { get; set; }

    /// <summary>Фоновая задача расшифровки, поданная заданием.</summary>
    public Guid? TranscriptTaskId { get; set; }

    /// <summary>Навигация к заданию.</summary>
    public MediaImportJob? Job { get; set; }

    /// <summary>Навигация к носителю.</summary>
    public MediaAsset? Asset { get; set; }
}
