using ISC.AI.Abstractions.Entities;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Domain.Enums;

namespace ISC.AI.Profile.Investigation.Domain.Entities;

/// <summary>
/// Дело (<c>investigation.case_file</c>, ТФ-ДЕЛ-01): единица контекста, в котором только и возможны загрузка
/// материалов и поиск по лицу (ТБ-071). Гриф и подразделение — БЕЗ умолчаний (ТБ-024): наследуются
/// носителями, фигурантами и шаблонами. Следователь — слабая ссылка на <c>core.app_user</c> (ТО-инф-06).
/// </summary>
public class CaseFile : AuditableEntity, IClassified
{
    /// <summary>Номер дела (уникален в подразделении).</summary>
    public required string Number { get; set; }

    /// <summary>Краткое название/фабула для списков.</summary>
    public required string Title { get; set; }

    /// <summary>Вид дела.</summary>
    public CaseKind Kind { get; set; }

    /// <summary>Дата возбуждения/регистрации.</summary>
    public DateOnly OpenedAt { get; set; }

    /// <summary>Следователь, ведущий дело (слабая ссылка на пользователя ядра).</summary>
    public int? InvestigatorUserId { get; set; }

    /// <summary>Подразделение (ТБ-020). NOT NULL.</summary>
    public int DivisionId { get; set; }

    /// <summary>Гриф (ТБ-020). NOT NULL.</summary>
    public short Classification { get; set; }

    /// <summary>Статус.</summary>
    public CaseStatus Status { get; set; } = CaseStatus.InProgress;

    /// <summary>Основание ведения дела (реквизиты постановления и т.п.).</summary>
    public string? Basis { get; set; }

    /// <summary>Когда закрыто (UTC).</summary>
    public DateTime? ClosedAt { get; set; }

    /// <summary>Кто создал запись (слабая ссылка).</summary>
    public int? CreatedByUserId { get; set; }
}
