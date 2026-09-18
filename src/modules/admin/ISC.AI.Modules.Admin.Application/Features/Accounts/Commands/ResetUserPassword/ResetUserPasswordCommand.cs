using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Admin.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.Admin.Application.Features.Accounts;

/// <summary>Сбросить пароль на временный; в ответе — новый временный пароль.</summary>
public sealed record ResetUserPasswordCommand(int UserId) : IRequest<ResponseDto<string>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"admin:account:{UserId}:reset-password";

    /// <inheritdoc cref="ResetUserPasswordCommand" />
    public sealed class Handler(IUserAccountStore accounts, IPlatformAdministration administration)
        : IRequestHandler<ResetUserPasswordCommand, ResponseDto<string>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<string>> Handle(
            ResetUserPasswordCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            // ИНВАРИАНТ (ТБ-012): сброс чужого пароля — администрирование, право даёт профиль.
            if (!await administration.CanManageAsync(cancellationToken))
            {
                return ResponseDto<string>.BadRequest(AdminGuard.Denied);
            }

            var temporary = TemporaryPassword.Generate();
            return await accounts.ResetPasswordAsync(command.UserId, temporary, cancellationToken)
                ? ResponseDto<string>.Ok(
                    temporary, "Пароль сброшен, сессии пользователя завершены. Передайте пароль лично.")
                : ResponseDto<string>.NotFound("Учётная запись не найдена.");
        }
    }
}
