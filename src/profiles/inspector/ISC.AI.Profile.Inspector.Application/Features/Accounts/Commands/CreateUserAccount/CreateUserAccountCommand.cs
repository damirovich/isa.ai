using System.Security.Cryptography;
using FluentValidation;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Inspector.Domain.Enums;
using ISC.AI.Profile.Inspector.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Inspector.Application.Features.Accounts;

/// <summary>Создать учётную запись; в ответе — ВРЕМЕННЫЙ пароль (показывается один раз).</summary>
public sealed record CreateUserAccountCommand(string UserName, string? DisplayName)
    : IRequest<ResponseDto<string>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    /// <remarks>Пароль в журнал НЕ пишется — только факт создания и имя входа (ТБ-043).</remarks>
    public string? AuditSummary => $"inspector:account:create:{UserName}";

    /// <inheritdoc cref="CreateUserAccountCommand" />
    public sealed class Handler(
        IUserAccountStore accounts, IUserRoleStore roles, ISubjectProvider subjectProvider)
        : IRequestHandler<CreateUserAccountCommand, ResponseDto<string>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<string>> Handle(
            CreateUserAccountCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (!await AccountGuard.CallerCanManageAsync(roles, subjectProvider, cancellationToken))
            {
                return ResponseDto<string>.BadRequest(AccountGuard.Denied);
            }

            var temporary = TemporaryPassword.Generate();
            var userId = await accounts.CreateAsync(
                command.UserName, command.DisplayName, temporary, cancellationToken);

            return userId is null
                ? ResponseDto<string>.BadRequest("Имя входа уже занято.")
                : ResponseDto<string>.Ok(temporary, "Учётная запись создана. Передайте временный пароль лично.");
        }
    }
}
