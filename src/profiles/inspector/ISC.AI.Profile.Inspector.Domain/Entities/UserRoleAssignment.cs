using ISC.AI.Abstractions.Entities;
using ISC.AI.Profile.Inspector.Domain.Enums;

namespace ISC.AI.Profile.Inspector.Domain.Entities;

/// <summary>
/// Роль пользователя (ТЗ СКИД §2.1, этап 6 Э4-35) — слабая ссылка на <c>core.app_user.Id</c> по значению,
/// БЕЗ FK через границу схем (ТО-инф-06). Один пользователь — одна роль (уникальный индекс на
/// <see cref="UserId"/>); без строки — роль не назначена (default-deny, см. <c>InspectorAccessPolicy</c>).
/// </summary>
public class UserRoleAssignment : AuditableEntity
{
    /// <summary>Пользователь (слабая ссылка на <c>core.app_user.Id</c>).</summary>
    public int UserId { get; set; }

    /// <summary>Назначенная роль.</summary>
    public UserRole Role { get; set; }
}
