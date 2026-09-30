using ISC.AI.Abstractions.Entities;
using ISC.AI.Profile.Investigation.Domain.Enums;

namespace ISC.AI.Profile.Investigation.Domain.Entities;

/// <summary>
/// Ячейка матрицы доступа, отличающаяся от умолчания поставки (<c>investigation.role_permission</c>, ADR-0032).
/// Нет строки — действует умолчание <c>InvestigationPermissions</c>; сброс к умолчаниям — удаление строк.
/// </summary>
public class RolePermission : AuditableEntity
{
    /// <summary>Роль.</summary>
    public InvestigationRole Role { get; set; }

    /// <summary>Ключ права (<c>InvestigationPermissions</c>).</summary>
    public string Permission { get; set; } = string.Empty;

    /// <summary>Открыто ли право роли.</summary>
    public bool IsGranted { get; set; }

    /// <summary>Кто последним изменил ячейку (слабая ссылка на <c>core.app_user</c>, ТО-инф-08).</summary>
    public int? UpdatedByUserId { get; set; }
}
