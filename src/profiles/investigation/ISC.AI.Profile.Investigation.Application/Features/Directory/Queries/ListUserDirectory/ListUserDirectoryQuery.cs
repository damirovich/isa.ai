using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Application.Features.Common;
using ISC.AI.Profile.Investigation.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Investigation.Application.Features.Directory;

/// <summary>
/// Справочник сотрудников профиля: активные и неактивные учётные записи ядра для ВЫБОРА следователя
/// в форме дела (ТФ-ДЕЛ-01) и для подписи «кто завёл» в карточках дела и фигуранта.
/// </summary>
/// <remarks>
/// ПОЧЕМУ ОТДЕЛЬНЫЙ ЗАПРОС, А НЕ <c>ListUserAccountsQuery</c> ПАКЕТА администрирования: у пакета тот
/// же список — режимные сведения, и он открыт только распорядителю (ТБ-012, <c>CanManageAsync</c>).
/// Здесь список нужен рядовому Следователю, чтобы завести дело, поэтому охрана другая и слабее:
/// достаточно ЛЮБОЙ роли профиля (ТП-004). Подменять одно другим нельзя ни в ту, ни в другую сторону:
/// открыть администраторский перечень всем — расширить доступ к режимным сведениям, а сузить этот —
/// запереть регистрацию дел. Отдаётся только то, что и так видно в интерфейсе: номер и имя.
/// </remarks>
public sealed record ListUserDirectoryQuery : IRequest<ResponseDto<IReadOnlyList<UserAccountRow>>>
{
    /// <inheritdoc cref="ListUserDirectoryQuery" />
    public sealed class Handler(IUserAccountStore accounts, IUserRoleStore roles, ISubjectProvider subjectProvider)
        : IRequestHandler<ListUserDirectoryQuery, ResponseDto<IReadOnlyList<UserAccountRow>>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<IReadOnlyList<UserAccountRow>>> Handle(
            ListUserDirectoryQuery query, CancellationToken cancellationToken)
        {
            // ИНВАРИАНТ (ТП-004): без роли в профиле субъект дел не ведёт — и перечня сотрудников ему
            // не нужно. Режима первичной настройки здесь нет: он относится к назначению ролей.
            if (!await RoleGuard.CallerHasAnyRoleAsync(roles, subjectProvider, cancellationToken))
            {
                return ResponseDto<IReadOnlyList<UserAccountRow>>.BadRequest(RoleGuard.NoRoleDenied);
            }

            var items = await accounts.ListAsync(cancellationToken);
            return ResponseDto<IReadOnlyList<UserAccountRow>>.Ok(items, items.Count);
        }
    }
}
