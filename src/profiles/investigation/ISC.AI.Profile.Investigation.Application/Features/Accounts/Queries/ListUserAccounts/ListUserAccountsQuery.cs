using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Application.Features.Common;
using ISC.AI.Profile.Investigation.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Investigation.Application.Features.Accounts;

/// <summary>
/// Учётные записи ядра — для выбора следователя в форме дела (ТФ-ДЕЛ-01). Открыто любому вошедшему
/// С РОЛЬЮ профиля: без роли субъект не ведёт дела и списка сотрудников ему не нужно.
/// </summary>
public sealed record ListUserAccountsQuery : IRequest<ResponseDto<IReadOnlyList<UserAccountRow>>>
{
    /// <inheritdoc cref="ListUserAccountsQuery" />
    public sealed class Handler(IUserAccountStore accounts, IUserRoleStore roles, ISubjectProvider subjectProvider)
        : IRequestHandler<ListUserAccountsQuery, ResponseDto<IReadOnlyList<UserAccountRow>>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<IReadOnlyList<UserAccountRow>>> Handle(
            ListUserAccountsQuery query, CancellationToken cancellationToken)
        {
            if (!await RoleGuard.CallerHasAnyRoleAsync(roles, subjectProvider, cancellationToken))
            {
                return ResponseDto<IReadOnlyList<UserAccountRow>>.BadRequest(RoleGuard.NoRoleDenied);
            }

            var items = await accounts.ListAsync(cancellationToken);
            return ResponseDto<IReadOnlyList<UserAccountRow>>.Ok(items, items.Count);
        }
    }
}
