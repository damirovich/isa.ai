namespace ISC.AI.Profile.Inspector.Domain.Enums;

/// <summary>
/// Роль пользователя для построчного доступа к документам docflow (ТЗ СКИД §2.1, этап 6 Э4-35).
/// Локальное понятие профиля «Инспектор» — ядро и модуль docflow о нём не знают (реализация подключается
/// через нейтральный <c>IAccessPolicy</c>, ADR-0014); другой профиль вправе завести свою схему ролей
/// или не заводить её вовсе, не трогая ядро/модуль.
/// </summary>
public enum UserRole
{
    /// <summary>Доступ к документам ПОЛНОСТЬЮ закрыт; ведёт пользователей, справочники, журнал действий.</summary>
    Administrator,

    /// <summary>Видит ВСЕ документы системы; единственный, кто снимает документ с контроля.</summary>
    Manager,

    /// <summary>Только документы, где сам назначен инспектором (включая отчёты и дашборд).</summary>
    Inspector,

    /// <summary>Только документы, где у него есть хотя бы одно назначение (поручение).</summary>
    Performer,
}

/// <summary>Русские подписи ролей для UI.</summary>
public static class UserRoleLabels
{
    /// <summary>Отображаемое название роли.</summary>
    public static string Label(this UserRole role) => role switch
    {
        UserRole.Administrator => "Администратор",
        UserRole.Manager => "Руководитель",
        UserRole.Inspector => "Инспектор",
        UserRole.Performer => "Исполнитель",
        _ => role.ToString(),
    };
}
