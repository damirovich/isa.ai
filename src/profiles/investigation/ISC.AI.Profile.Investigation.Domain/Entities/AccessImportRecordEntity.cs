using ISC.AI.Abstractions.Entities;

namespace ISC.AI.Profile.Investigation.Domain.Entities;

/// <summary>
/// Перенесённая запись базы Access (<c>investigation.access_import_record</c>, ADR-0043): какой файл (SHA-256), какая
/// таблица и запись — и какое дело и фигурант из неё созданы. Повтор переноса той же базы пропускает перенесённое.
/// </summary>
/// <remarks>
/// Дело — FK внутри схемы с каскадом: уничтожение дела (ADR-0025) уносит и его соответствия, повторный перенос заведёт
/// дело заново. Фигурант — значение без FK: он удаляется вместе с делом.
/// </remarks>
public class AccessImportRecordEntity : BaseEntity
{
    /// <summary>SHA-256 файла базы (hex, верхний регистр).</summary>
    public required string FileSha256 { get; set; }

    /// <summary>Таблица базы.</summary>
    public required string SourceTable { get; set; }

    /// <summary>Код записи (или № задания, если кода нет).</summary>
    public required string SourceKey { get; set; }

    /// <summary>Дело.</summary>
    public int CaseId { get; set; }

    /// <summary>Навигация к делу.</summary>
    public CaseFile? Case { get; set; }

    /// <summary>Фигурант (объект или связь).</summary>
    public int? PersonId { get; set; }
}
