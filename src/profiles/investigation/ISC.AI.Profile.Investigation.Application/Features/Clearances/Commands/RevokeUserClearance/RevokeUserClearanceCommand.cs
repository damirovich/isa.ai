using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Application.Features.Common;
using ISC.AI.Profile.Investigation.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Investigation.Application.Features.Clearances;

/// <summary>Отозвать допуск пользователя — действует немедленно (ТБ-016).</summary>
public sealed record RevokeUserClearanceCommand(int UserId) : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"investigation:clearance:{UserId}:revoke";

    /// <inheritdoc cref="RevokeUserClearanceCommand" />
    public sealed class Handler(IClearanceStore clearances, IUserRoleStore roles, ISubjectProvider subjectProvider)
        : IRequestHandler<RevokeUserClearanceCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(RevokeUserClearanceCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (!await RoleGuard.CallerCanManageAsync(roles, subjectProvider, cancellationToken))
            {
                return ResponseDto<bool>.BadRequest(RoleGuard.AdminDenied);
            }

            return await clearances.RevokeAsync(command.UserId, cancellationToken)
                ? ResponseDto<bool>.Ok(true)
                : ResponseDto<bool>.NotFound("У пользователя нет действующего допуска.");
        }
    }
}
