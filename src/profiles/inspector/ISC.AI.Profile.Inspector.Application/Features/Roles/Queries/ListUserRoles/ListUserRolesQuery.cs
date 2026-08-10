using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Inspector.Domain.Enums;
using ISC.AI.Profile.Inspector.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Inspector.Application.Features.Roles;

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
    public sealed class Handler(IUserRoleStore store, ISubjectProvider subjectProvider)
        : IRequestHandler<ListUserRolesQuery, ResponseDto<IReadOnlyList<UserRoleRow>>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<IReadOnlyList<UserRoleRow>>> Handle(
            ListUserRolesQuery query, CancellationToken cancellationToken)
        {
            if (!await RoleScenariosGuard.CallerCanManageRolesAsync(store, subjectProvider, cancellationToken))
            {
                return ResponseDto<IReadOnlyList<UserRoleRow>>.BadRequest(
                    "Список и назначение ролей доступны только Администратору.");
            }

            var items = await store.ListAsync(cancellationToken);
            return ResponseDto<IReadOnlyList<UserRoleRow>>.Ok(items, items.Count);
        }
    }
}
