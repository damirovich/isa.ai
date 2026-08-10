using System.Security.Cryptography;
using FluentValidation;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Inspector.Domain.Enums;
using ISC.AI.Profile.Inspector.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Inspector.Application.Features.Accounts;

/// <summary>Сбросить пароль на временный; в ответе — новый временный пароль.</summary>
public sealed record ResetUserPasswordCommand(int UserId) : IRequest<ResponseDto<string>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"inspector:account:{UserId}:reset-password";

    /// <inheritdoc cref="ResetUserPasswordCommand" />
    public sealed class Handler(
        IUserAccountStore accounts, IUserRoleStore roles, ISubjectProvider subjectProvider)
        : IRequestHandler<ResetUserPasswordCommand, ResponseDto<string>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<string>> Handle(
            ResetUserPasswordCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (!await AccountGuard.CallerCanManageAsync(roles, subjectProvider, cancellationToken))
            {
                return ResponseDto<string>.BadRequest(AccountGuard.Denied);
            }

            var temporary = TemporaryPassword.Generate();
            return await accounts.ResetPasswordAsync(command.UserId, temporary, cancellationToken)
                ? ResponseDto<string>.Ok(
                    temporary, "Пароль сброшен, сессии пользователя завершены. Передайте пароль лично.")
                : ResponseDto<string>.NotFound("Учётная запись не найдена.");
        }
    }
}
