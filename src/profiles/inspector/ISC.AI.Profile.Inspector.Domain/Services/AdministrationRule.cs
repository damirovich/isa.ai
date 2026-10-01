using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Inspector.Domain.Enums;

namespace ISC.AI.Profile.Inspector.Domain.Services;

/// <summary>
/// Кто вправе вести пользователей, роли и допуски — право <see cref="InspectorPermissions.AdminUsers"/> матрицы
/// доступа, закреплённое за Администратором (ADR-0033). Остальные права администрирования (журнал, настройки
/// документооборота, картотека НПА, виды нарушений) спрашиваются у <see cref="PermissionRule"/> по своему ключу.
/// </summary>
/// <remarks>
/// Само правило: Администратор — всегда; любой вошедший — ТОЛЬКО пока Администратора в системе нет.
/// Вторая половина не послабление, а выход из «замка без ключа» (6.4.1): после чистого развёртывания
/// ролей не назначено никому, и без неё систему невозможно было бы настроить вообще.
/// </remarks>
public static class AdministrationRule
{
    /// <summary>Вправе ли текущий субъект вести пользователей, роли и допуски.</summary>
    public static Task<bool> CallerCanManageAsync(
        IUserRoleStore roles, ISubjectProvider subjectProvider, CancellationToken cancellationToken = default) =>
        PermissionRule.CallerHasAsync(roles, subjectProvider, InspectorPermissions.AdminUsers, cancellationToken);
}

/// <summary>
/// Инвариант назначения ролей профиля «ИнспекторAI» (ТБ-012, 6.4.1): последнего действующего Администратора снять
/// нельзя. Одно место для сценария профиля и для порта назначения ролей пакета администрирования — правило не
/// расходится между экранами (так же, как в профиле «Следствие»).
/// </summary>
public static class RoleAssignmentRule
{
    /// <summary>Текст отказа при попытке снять последнего Администратора.</summary>
    public const string LastAdministrator =
        "Нельзя снять роль с последнего Администратора: система осталась бы без управления ролями и допусками. "
        + "Сначала назначьте Администратором другого сотрудника.";

    /// <summary>
    /// Причина отказа назначить <paramref name="newRole"/> пользователю <paramref name="userId"/>; можно — <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// ИНВАРИАНТ: последний Администратор неснимаем. Иначе окно первичной настройки (<see cref="PermissionRule"/>)
    /// открылось бы заново для ЛЮБОГО вошедшего — пользователи, роли, допуски и матрица стали бы доступны всем.
    /// Считаются только ДЕЙСТВУЮЩИЕ учётные записи (<see cref="IUserRoleStore.ListAsync"/>): Администратор с отключённой
    /// записью войти не может, и опираться на него — тот же «замок без ключа».
    /// </remarks>
    public static async Task<string?> CheckAsync(
        IUserRoleStore roles, int userId, UserRole? newRole, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roles);

        if (newRole == UserRole.Administrator
            || await roles.GetRoleAsync(userId, cancellationToken) != UserRole.Administrator)
        {
            return null;
        }

        var others = (await roles.ListAsync(cancellationToken))
            .Count(row => row.Role == UserRole.Administrator && row.UserId != userId);
        return others == 0 ? LastAdministrator : null;
    }
}
