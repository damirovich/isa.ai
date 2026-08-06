using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Inspector.Domain.Enums;
using ISC.AI.Profile.Inspector.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Inspector.Application.Roles;

/// <summary>
/// Сценарии ведения ролей пользователей (§2.1 ТЗ СКИД, этап 6 Э4-35). Проверка «вызывающий —
/// Администратор» — ЗДЕСЬ, в обработчике, а не только на странице UI (тот же урок, что аудит слияния
/// вынес для write-сценариев докфлоу без проверки допуска, Э4-35 §6.2): без серверной проверки первый
/// же новый вызывающий (другая страница, будущий API) обошёл бы ограничение.
/// </summary>

/// <summary>Все пользователи с их текущей ролью (для страницы администрирования).</summary>
public sealed record ListUserRolesQuery : IRequest<ResponseDto<IReadOnlyList<UserRoleRow>>>
{
    /// <inheritdoc cref="ListUserRolesQuery" />
    public sealed class Handler(IUserRoleStore store, IAccessContextProvider accessProvider)
        : IRequestHandler<ListUserRolesQuery, ResponseDto<IReadOnlyList<UserRoleRow>>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<IReadOnlyList<UserRoleRow>>> Handle(
            ListUserRolesQuery query, CancellationToken cancellationToken)
        {
            if (!await RoleScenariosGuard.CallerIsAdministratorAsync(store, accessProvider, cancellationToken))
            {
                return ResponseDto<IReadOnlyList<UserRoleRow>>.BadRequest(
                    "Список и назначение ролей доступны только Администратору.");
            }

            var items = await store.ListAsync(cancellationToken);
            return ResponseDto<IReadOnlyList<UserRoleRow>>.Ok(items, items.Count);
        }
    }
}

/// <summary>Назначить роль пользователю (<paramref name="Role"/> = <see langword="null"/> — снять роль).</summary>
public sealed record SetUserRoleCommand(int UserId, UserRole? Role) : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"inspector:user-role:{UserId}:{(Role is { } r ? r.ToString() : "снята")}";

    /// <inheritdoc cref="SetUserRoleCommand" />
    public sealed class Handler(IUserRoleStore store, IAccessContextProvider accessProvider)
        : IRequestHandler<SetUserRoleCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(SetUserRoleCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (!await RoleScenariosGuard.CallerIsAdministratorAsync(store, accessProvider, cancellationToken))
            {
                return ResponseDto<bool>.BadRequest("Назначение ролей доступно только Администратору.");
            }

            await store.SetRoleAsync(command.UserId, command.Role, cancellationToken);
            return ResponseDto<bool>.Ok(true);
        }
    }
}

/// <summary>Общая проверка вызывающего для обоих сценариев (см. remarks класса).</summary>
file static class RoleScenariosGuard
{
    public static async Task<bool> CallerIsAdministratorAsync(
        IUserRoleStore store, IAccessContextProvider accessProvider, CancellationToken cancellationToken)
    {
        var access = await accessProvider.GetCurrentAsync(cancellationToken);
        if (access.NumericSubjectId is not { } callerId)
        {
            return false;
        }

        return await store.GetRoleAsync(callerId, cancellationToken) == UserRole.Administrator;
    }
}
