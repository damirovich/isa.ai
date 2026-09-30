using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Domain.Enums;

namespace ISC.AI.Profile.Investigation.Domain.Services;

/// <summary>Строка реестра ролей: пользователь ядра и его роль (или её отсутствие).</summary>
public sealed record UserRoleRow(int UserId, string DisplayName, InvestigationRole? Role);

/// <summary>Реестр ролей профиля (ТП-004). Реализация — <c>Investigation.Data</c> (две схемы: core.app_user + investigation.user_role_assignment).</summary>
public interface IUserRoleStore
{
    /// <summary>Роль пользователя; <see langword="null"/> — не назначена.</summary>
    Task<InvestigationRole?> GetRoleAsync(int userId, CancellationToken cancellationToken = default);

    /// <summary>Есть ли хотя бы один Администратор (основа режима первичной настройки).</summary>
    Task<bool> AnyAdministratorAsync(CancellationToken cancellationToken = default);

    /// <summary>Все активные пользователи ядра с ролями, по имени.</summary>
    Task<IReadOnlyList<UserRoleRow>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Пользователи с указанной ролью.</summary>
    Task<IReadOnlyList<int>> ListUserIdsByRoleAsync(InvestigationRole role, CancellationToken cancellationToken = default);

    /// <summary>Назначить роль (<see langword="null"/> — снять).</summary>
    Task SetRoleAsync(int userId, InvestigationRole? role, CancellationToken cancellationToken = default);

    /// <summary>
    /// Сохранённое отличие ячейки матрицы доступа от умолчания (ADR-0032); нет отличия — <see langword="null"/>
    /// (действует умолчание <see cref="InvestigationPermissions"/>).
    /// </summary>
    Task<bool?> GetPermissionOverrideAsync(InvestigationRole role, string permission, CancellationToken cancellationToken = default);

    /// <summary>Все сохранённые отличия матрицы доступа от умолчаний.</summary>
    Task<IReadOnlyList<RolePermissionOverride>> ListPermissionOverridesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Применить изменения ячеек одной транзакцией: значение — записать отличие, <see langword="null"/> — удалить (вернуть
    /// к умолчанию). Проверку замков и права делает сценарий, хранилище пишет как велено.
    /// </summary>
    Task ApplyPermissionChangesAsync(
        IReadOnlyCollection<RolePermissionChange> changes, int? changedByUserId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Правило «кто вправе вести пользователей, роли и допуски»: Администратор — всегда; пока Администратора нет ни
/// одного — любой вошедший (режим первичной настройки: иначе на чистом контуре «замок без ключа»). Это право
/// <see cref="InvestigationPermissions.AdminUsers"/> матрицы доступа — замкнутое за Администратором (ADR-0032);
/// остальные права администрирования спрашиваются у <see cref="PermissionRule"/> по своему ключу.
/// </summary>
public static class AdministrationRule
{
    /// <summary>Текст отказа для ответов сценариев.</summary>
    public const string Denied = "Действие доступно только Администратору.";

    /// <summary>Вправе ли текущий субъект вести пользователей, роли и допуски.</summary>
    public static Task<bool> CallerCanManageAsync(
        IUserRoleStore roles, ISubjectProvider subjectProvider, CancellationToken cancellationToken = default) =>
        PermissionRule.CallerHasAsync(roles, subjectProvider, InvestigationPermissions.AdminUsers, cancellationToken);
}

/// <summary>
/// Инвариант назначения ролей профиля «Следствие» (ТП-004): последнего Администратора снять нельзя. Одно место для
/// сценария профиля и для порта назначения ролей пакета администрирования — правило не расходится между экранами.
/// </summary>
public static class RoleAssignmentRule
{
    /// <summary>Текст отказа при попытке снять последнего Администратора.</summary>
    public const string LastAdministrator =
        "Нельзя снять роль с последнего Администратора: система осталась бы без управления ролями и допусками.";

    /// <summary>
    /// Причина отказа назначить <paramref name="newRole"/> пользователю <paramref name="userId"/>; можно — <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// ИНВАРИАНТ: последний Администратор неснимаем. Иначе окно первичной настройки открылось бы заново для ЛЮБОГО
    /// вошедшего (самовосстановление <see cref="AdministrationRule"/> обернулось бы дырой), а до этого — «замок без ключа».
    /// </remarks>
    public static async Task<string?> CheckAsync(
        IUserRoleStore roles, int userId, InvestigationRole? newRole, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roles);

        if (newRole == InvestigationRole.Administrator
            || await roles.GetRoleAsync(userId, cancellationToken) != InvestigationRole.Administrator)
        {
            return null;
        }

        var administrators = await roles.ListUserIdsByRoleAsync(InvestigationRole.Administrator, cancellationToken);
        return administrators.Count <= 1 ? LastAdministrator : null;
    }
}
