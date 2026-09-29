using ISC.AI.Abstractions.Entities;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Domain.Enums;

namespace ISC.AI.Profile.Investigation.Domain.Entities;

/// <summary>
/// Сводка или справка по бланку (<c>investigation.case_report</c>, ТФ-ДДЛ-04, ADR-0031). Шапка документа:
/// поля бланка живут в редакциях (<see cref="CaseReportRevision"/>). Гриф и подразделение — с дела (ТБ-070).
/// Удаления нет: только <see cref="IsActive"/> = <see langword="false"/> (ТФ-ДДЛ-05).
/// </summary>
public class CaseReport : AuditableEntity, IClassified
{
    /// <summary>Дело (FK; уничтожение дела уносит документ, ADR-0025).</summary>
    public int CaseId { get; set; }

    /// <summary>Навигация к делу.</summary>
    public CaseFile? Case { get; set; }

    /// <summary>Объект — фигурант этого дела (необязательно).</summary>
    public int? PersonId { get; set; }

    /// <summary>Вид документа.</summary>
    public CaseReportKind Kind { get; set; }

    /// <summary>Дата, за которую составлен документ («папка даты»).</summary>
    public DateOnly ReportDate { get; set; }

    /// <summary>Активен (неактивный хранится, но не правится).</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Автор (кто создал).</summary>
    public int? CreatedByUserId { get; set; }

    /// <summary>Номер текущей (актуальной) редакции.</summary>
    public int CurrentRevision { get; set; }

    /// <summary>Нормализованный текст полей текущей редакции для поиска подстрокой (ТО-мат-11).</summary>
    public string SearchText { get; set; } = string.Empty;

    /// <summary>Гриф (с дела). NOT NULL.</summary>
    public short Classification { get; set; }

    /// <summary>Подразделение (с дела). NOT NULL.</summary>
    public int DivisionId { get; set; }
}

/// <summary>
/// Редакция документа (<c>investigation.case_report_revision</c>, ТФ-ДДЛ-05): только добавляется. Актуальна
/// редакция с номером <see cref="CaseReport.CurrentRevision"/>; прежние — «неактуальные», доступны в истории.
/// </summary>
public class CaseReportRevision : AuditableEntity, IClassified
{
    /// <summary>Документ (FK, каскад).</summary>
    public int ReportId { get; set; }

    /// <summary>Навигация к документу.</summary>
    public CaseReport? Report { get; set; }

    /// <summary>Номер редакции с 1; уникален в документе.</summary>
    public int Number { get; set; }

    /// <summary>Поля бланка (JSON, <c>jsonb</c>).</summary>
    public required string ContentJson { get; set; }

    /// <summary>Автор редакции.</summary>
    public int? AuthorUserId { get; set; }

    /// <summary>Причина правки после окна (из разрешения); в окне — пусто.</summary>
    public string? EditReason { get; set; }

    /// <summary>Разрешение, по которому сделана правка после окна.</summary>
    public int? PermitId { get; set; }

    /// <summary>Гриф (с документа). NOT NULL.</summary>
    public short Classification { get; set; }

    /// <summary>Подразделение (с документа). NOT NULL.</summary>
    public int DivisionId { get; set; }
}

/// <summary>
/// Запрос на правку архивного документа и решение Администратора (<c>investigation.case_report_permit</c>,
/// ТФ-ДДЛ-05, ТФ-АДМ-06). Причина обязательна; разрешение действует ограниченное время и только для того,
/// кто просил. Администратор видит только эти метаданные, не поля бланка (ТБ-079).
/// </summary>
public class CaseReportPermit : AuditableEntity, IClassified
{
    /// <summary>Документ (FK, каскад).</summary>
    public int ReportId { get; set; }

    /// <summary>Навигация к документу.</summary>
    public CaseReport? Report { get; set; }

    /// <summary>Кто просит.</summary>
    public int RequestedByUserId { get; set; }

    /// <summary>Причина правки (обязательна).</summary>
    public required string Reason { get; set; }

    /// <summary>Когда подан запрос (UTC).</summary>
    public DateTime RequestedAt { get; set; }

    /// <summary>Статус.</summary>
    public ReportEditPermitStatus Status { get; set; } = ReportEditPermitStatus.Pending;

    /// <summary>Кто решил.</summary>
    public int? DecidedByUserId { get; set; }

    /// <summary>Когда решено (UTC).</summary>
    public DateTime? DecidedAt { get; set; }

    /// <summary>До какого момента действует разрешение (UTC); только у разрешённых.</summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>Гриф (с документа). NOT NULL.</summary>
    public short Classification { get; set; }

    /// <summary>Подразделение (с документа). NOT NULL.</summary>
    public int DivisionId { get; set; }
}
