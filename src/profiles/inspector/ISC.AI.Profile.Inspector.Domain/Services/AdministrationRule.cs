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
