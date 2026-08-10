using FluentValidation;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Inspector.Domain.Enums;
using ISC.AI.Profile.Inspector.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Inspector.Application.Features.Clearances;

/// <summary>Все активные пользователи с их допусками (экран администрирования).</summary>
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
            if (!await ClearanceGuard.CallerCanManageAsync(roles, subjectProvider, cancellationToken))
            {
                return ResponseDto<IReadOnlyList<UserClearanceRow>>.BadRequest(ClearanceGuard.Denied);
            }

            var directory = (await divisions.ListAsync(cancellationToken))
                .ToDictionary(d => d.Id, d => d.Name);

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
