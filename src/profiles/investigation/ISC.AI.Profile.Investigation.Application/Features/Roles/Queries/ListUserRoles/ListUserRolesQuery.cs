using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Application.Features.Common;
using ISC.AI.Profile.Investigation.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Investigation.Application.Features.Roles;

/// <summary>Реестр ролей: пользователи с их ролями и признак режима первичной настройки.</summary>
/// <param name="Users">Активные пользователи ядра с ролью профиля (или без неё).</param>
/// <param name="IsBootstrap">
/// В системе НЕТ ни одного Администратора: роли вправе назначать любой вошедший — страница обязана
/// показать это явно, чтобы первый Администратор был назначен осознанно и окно закрылось.
/// </param>
public sealed record UserRoleRegistry(IReadOnlyList<UserRoleRow> Users, bool IsBootstrap);

/// <summary>
/// Все пользователи с их текущей ролью (ТФ-АДМ-01, ТП-004). Проверка «вызывающий — Администратор» — в
/// обработчике, а не только на странице: без серверной проверки любой новый вызывающий обошёл бы ограничение.
/// </summary>
public sealed record ListUserRolesQuery : IRequest<ResponseDto<UserRoleRegistry>>
{
    /// <inheritdoc cref="ListUserRolesQuery" />
    public sealed class Handler(IUserRoleStore roles, ISubjectProvider subjectProvider)
        : IRequestHandler<ListUserRolesQuery, ResponseDto<UserRoleRegistry>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<UserRoleRegistry>> Handle(
            ListUserRolesQuery query, CancellationToken cancellationToken)
        {
            if (!await RoleGuard.CallerCanManageAsync(roles, subjectProvider, cancellationToken))
            {
                return ResponseDto<UserRoleRegistry>.BadRequest(RoleGuard.AdminDenied);
            }

            var users = await roles.ListAsync(cancellationToken);
            var isBootstrap = !await roles.AnyAdministratorAsync(cancellationToken);
            return ResponseDto<UserRoleRegistry>.Ok(new UserRoleRegistry(users, isBootstrap), users.Count);
        }
    }
}
