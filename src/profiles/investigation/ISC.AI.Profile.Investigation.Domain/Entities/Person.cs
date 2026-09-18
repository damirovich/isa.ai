using ISC.AI.Abstractions.Entities;
using ISC.AI.Abstractions.Security;

namespace ISC.AI.Profile.Investigation.Domain.Entities;

/// <summary>
/// Фигурант дела (<c>investigation.person</c>, ТФ-ПЕР-01): установочные данные могут быть неизвестны —
/// тогда «неустановленное лицо № N». Гриф/подразделение — дела (ТБ-070).
/// </summary>
public class Person : AuditableEntity, IClassified
{
    /// <summary>Дело (FK внутри схемы).</summary>
    public int CaseId { get; set; }

    /// <summary>Навигация к делу.</summary>
    public CaseFile? Case { get; set; }

    /// <summary>ФИО/установочные данные либо «Неустановленное лицо № N».</summary>
    public required string DisplayName { get; set; }

    /// <summary>Личность не установлена.</summary>
    public bool IsUnidentified { get; set; }

    /// <summary>Порядковый номер неустановленного лица в деле.</summary>
    public int? UnidentifiedNumber { get; set; }

    /// <summary>Роль в деле (подозреваемый, свидетель, потерпевший и т.п. — свободный текст по практике).</summary>
    public string? RoleInCase { get; set; }

    /// <summary>Примечания следователя.</summary>
    public string? Notes { get; set; }

    /// <summary>Гриф (денормализован с дела). NOT NULL.</summary>
    public short Classification { get; set; }

    /// <summary>Подразделение (денормализовано с дела). NOT NULL.</summary>
    public int DivisionId { get; set; }
}
