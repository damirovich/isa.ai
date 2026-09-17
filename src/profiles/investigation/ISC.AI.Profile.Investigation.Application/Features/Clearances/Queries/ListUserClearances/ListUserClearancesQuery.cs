using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Application.Features.Common;
using ISC.AI.Profile.Investigation.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Investigation.Application.Features.Clearances;

/// <summary>
/// Все активные пользователи с их допусками (ТФ-АДМ-01). Допуск — ядровое понятие (<c>core.clearance</c>,
/// ТБ-011/020/021), а правило «кто вправе выдавать» — профильное (Администратор, ТП-004).
/// </summary>
public sealed record ListUserClearancesQuery : IRequest<ResponseDto<IReadOnlyList<UserClearanceRow>>>
{
    /// <inheritdoc cref="ListUserClearancesQuery" />
    public sealed class Handler(
        IClearanceStore clearances,
        IDivisionAdminStore divisions,
        IUserRoleStore roles,
        ISubjectProvider subjectProvider)
        : IRequestHandler<ListUserClearancesQuery, ResponseDto<IReadOnlyList<UserClearanceRow>>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<IReadOnlyList<UserClearanceRow>>> Handle(
            ListUserClearancesQuery query, CancellationToken cancellationToken)
        {
            // Опора на «кто вошёл», а не на контекст допуска: у распорядителя допуска может ещё не быть.
            if (!await RoleGuard.CallerCanManageAsync(roles, subjectProvider, cancellationToken))
            {
                return ResponseDto<IReadOnlyList<UserClearanceRow>>.BadRequest(RoleGuard.AdminDenied);
            }

            var directory = (await divisions.ListAsync(cancellationToken)).ToDictionary(d => d.Id, d => d.Name);
            var rows = (await clearances.ListAsync(cancellationToken))
                .Select(row => new UserClearanceRow(
                    row.UserId,
                    row.DisplayName,
                    row.MaxClassification,
                    [
                        .. row.DivisionScope.Select(id => new ClearanceDivision(
                            id, directory.TryGetValue(id, out var name) ? name : null)),
                    ]))
                .ToList();
            return ResponseDto<IReadOnlyList<UserClearanceRow>>.Ok(rows, rows.Count);
        }
    }
}
