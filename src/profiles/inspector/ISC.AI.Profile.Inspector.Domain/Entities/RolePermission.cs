namespace ISC.AI.Profile.Inspector.Domain.Entities;

/// <summary>
/// Ячейка матрицы доступа, отличающаяся от умолчания поставки (<c>inspector.role_permission</c>, ADR-0033).
/// Нет строки — действует умолчание <c>InspectorPermissions</c>; сброс к умолчаниям — удаление строк.
/// </summary>
public class RolePermission : AuditableEntity
{
    /// <summary>Роль.</summary>
    public UserRole Role { get; set; }

    /// <summary>Ключ права (<c>InspectorPermissions</c>).</summary>
    public string Permission { get; set; } = string.Empty;

    /// <summary>Открыто ли право роли.</summary>
    public bool IsGranted { get; set; }

    /// <summary>Кто последним изменил ячейку (слабая ссылка на <c>core.app_user</c>, ТО-инф-06).</summary>
    public int? UpdatedByUserId { get; set; }
}
