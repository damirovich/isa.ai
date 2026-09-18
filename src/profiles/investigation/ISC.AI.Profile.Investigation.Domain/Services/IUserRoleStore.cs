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
}

/// <summary>
/// Единое правило «кто вправе администрировать» (роли, допуски, справочники, учётные записи):
/// Администратор — всегда; пока Администратора нет ни одного — любой вошедший (режим первичной настройки:
/// иначе на чистом контуре «замок без ключа»). Одно место для всех гвардов и для <c>IDocFlowAdministration</c>.
/// </summary>
public static class AdministrationRule
{
    /// <summary>Текст отказа для ответов сценариев.</summary>
    public const string Denied = "Действие доступно только Администратору.";

    /// <summary>Вправе ли текущий субъект администрировать.</summary>
    public static async Task<bool> CallerCanManageAsync(
        IUserRoleStore roles, ISubjectProvider subjectProvider, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(subjectProvider);

        if (await subjectProvider.GetCurrentUserIdAsync(cancellationToken) is not { } userId)
        {
            return false;
        }

        if (await roles.GetRoleAsync(userId, cancellationToken) == InvestigationRole.Administrator)
        {
            return true;
        }

        return !await roles.AnyAdministratorAsync(cancellationToken);
    }

    /// <summary>Есть ли у текущего субъекта одна из ролей (без режима первичной настройки — для операций с данными дел).</summary>
    public static async Task<bool> CallerHasRoleAsync(
        IUserRoleStore roles, ISubjectProvider subjectProvider, CancellationToken cancellationToken, params InvestigationRole[] allowed)
    {
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(subjectProvider);

        if (await subjectProvider.GetCurrentUserIdAsync(cancellationToken) is not { } userId)
        {
            return false;
        }

        var role = await roles.GetRoleAsync(userId, cancellationToken);
        return role is { } r && allowed.Contains(r);
    }
}
