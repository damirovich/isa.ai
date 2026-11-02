using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Admin.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.Admin.Application.Features.Accounts;

/// <summary>
/// Снять блокировку учётной записи после неудачных входов подряд (ТБ-081, ADR-0065). Пароль, роль, допуск и сеансы не
/// меняются; если пароль могли подобрать, правильнее сбросить его — сброс снимает и блокировку.
/// </summary>
public sealed record UnlockUserAccountCommand(int UserId) : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"admin:account:{UserId}:unlock";

    /// <inheritdoc cref="UnlockUserAccountCommand" />
    public sealed class Handler(IUserAccountStore accounts, IPlatformAdministration administration)
        : IRequestHandler<UnlockUserAccountCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(UnlockUserAccountCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            // ИНВАРИАНТ (ТБ-081, ТБ-012): восстановить доступ может только Администратор — право то же, что у включения и
            // отключения учётной записи, и знает его профиль. Запись в журнале — от AuditBehavior.
            if (!await administration.CanManageAsync(cancellationToken))
            {
                return ResponseDto<bool>.BadRequest(AdminGuard.Denied);
            }

            return await accounts.UnlockAsync(command.UserId, cancellationToken)
                ? ResponseDto<bool>.Ok(true)
                : ResponseDto<bool>.NotFound("Учётная запись не найдена.");
        }
    }
}
