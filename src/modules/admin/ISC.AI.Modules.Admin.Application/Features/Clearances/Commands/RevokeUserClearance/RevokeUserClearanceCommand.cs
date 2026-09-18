using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Admin.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.Admin.Application.Features.Clearances;

/// <summary>Отозвать допуск пользователя — действует немедленно (ТБ-016).</summary>
public sealed record RevokeUserClearanceCommand(int UserId) : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"admin:clearance:{UserId}:revoke";

    /// <inheritdoc cref="RevokeUserClearanceCommand" />
    public sealed class Handler(IClearanceStore clearances, IPlatformAdministration administration)
        : IRequestHandler<RevokeUserClearanceCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(
            RevokeUserClearanceCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            // ИНВАРИАНТ (ТБ-012/020): отзыв допуска — распоряжение доступом, право даёт ПРОФИЛЬ.
            if (!await administration.CanManageAsync(cancellationToken))
            {
                return ResponseDto<bool>.BadRequest(AdminGuard.Denied);
            }

            return await clearances.RevokeAsync(command.UserId, cancellationToken)
                ? ResponseDto<bool>.Ok(true)
                : ResponseDto<bool>.NotFound("У пользователя нет действующего допуска.");
        }
    }
}
