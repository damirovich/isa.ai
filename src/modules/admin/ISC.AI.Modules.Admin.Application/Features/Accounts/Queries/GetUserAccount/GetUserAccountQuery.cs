using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Admin.Application.Features.Clearances;
using ISC.AI.Modules.Admin.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.Admin.Application.Features.Accounts;

/// <summary>Карточка сотрудника на экране «Пользователи»: учётная запись, роль, допуск и справочники для их правки.</summary>
/// <param name="Account">Учётная запись с ролью.</param>
/// <param name="Roles">Роли профиля с описаниями — для выбора роли.</param>
/// <param name="DivisionCatalog">Все подразделения профиля (включая недействующие) — для правки допуска.</param>
/// <param name="ClearanceDivisions">
/// Подразделения из допуска сотрудника с наименованиями; номер, которого нет в справочнике, — без наименования
/// (<see cref="ClearanceDivision.IsKnown"/> = <see langword="false"/>): такой номер не даёт доступа ни к чему.
/// </param>
/// <param name="IsSelf">Это учётная запись самого администратора: отключить её нельзя.</param>
/// <param name="IsInitialSetup">Режим первичной настройки: Администратора в системе нет.</param>
public sealed record UserAccountCard(
    UserAccountView Account,
    IReadOnlyList<RoleOption> Roles,
    IReadOnlyList<ClearanceDivision> DivisionCatalog,
    IReadOnlyList<ClearanceDivision> ClearanceDivisions,
    bool IsSelf,
    bool IsInitialSetup);

/// <summary>Карточка одного сотрудника (включая отключённых) — экран «Пользователи».</summary>
/// <param name="UserId">Пользователь ядра.</param>
public sealed record GetUserAccountQuery(int UserId) : IRequest<ResponseDto<UserAccountCard>>
{
    /// <inheritdoc cref="GetUserAccountQuery" />
    public sealed class Handler(
        IUserAccountStore accounts,
        IUserRoleCatalog roleCatalog,
        IDivisionCatalog divisions,
        ISubjectProvider subjectProvider,
        IPlatformAdministration administration)
        : IRequestHandler<GetUserAccountQuery, ResponseDto<UserAccountCard>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<UserAccountCard>> Handle(GetUserAccountQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            // ИНВАРИАНТ (ТБ-012): учётная запись, роль и допуск сотрудника — режимные сведения, право даёт профиль.
            if (!await administration.CanManageAsync(cancellationToken))
            {
                return ResponseDto<UserAccountCard>.BadRequest(AdminGuard.Denied);
            }

            var page = await accounts.SearchAsync(
                new UserAccountFilter(RestrictToUserIds: [query.UserId], Page: 1, PageSize: 1), cancellationToken);
            if (page.Rows.FirstOrDefault(r => r.UserId == query.UserId) is not { } row)
            {
                return ResponseDto<UserAccountCard>.NotFound("Учётная запись не найдена.");
            }

            var roles = await roleCatalog.ListRolesAsync(cancellationToken);
            var roleKey = (await roleCatalog.GetUserRoleKeysAsync(cancellationToken)).TryGetValue(row.UserId, out var key) ? key : null;
            var roleLabel = roleKey is null ? null : roles.FirstOrDefault(r => r.Key == roleKey)?.Label;

            var catalog = (await divisions.ListAsync(cancellationToken))
                .Select(d => new ClearanceDivision(d.Id, d.Name))
                .ToList();
            var names = catalog.ToDictionary(d => d.Id, d => d.Name);
            var granted = (row.Divisions ?? [])
                .Distinct()
                .Select(id => new ClearanceDivision(id, names.TryGetValue(id, out var name) ? name : null))
                .ToList();

            var callerId = await subjectProvider.GetCurrentUserIdAsync(cancellationToken);
            var card = new UserAccountCard(
                new UserAccountView(row, roleKey, roleLabel),
                roles,
                catalog,
                granted,
                IsSelf: callerId == row.UserId,
                IsInitialSetup: await administration.IsInitialSetupAsync(cancellationToken));
            return ResponseDto<UserAccountCard>.Ok(card);
        }
    }
}
