using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Admin.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.Admin.Application.Features.Accounts;

/// <summary>Создать учётную запись; в ответе — ВРЕМЕННЫЙ пароль (показывается один раз).</summary>
public sealed record CreateUserAccountCommand(string UserName, string? DisplayName)
    : IRequest<ResponseDto<string>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    /// <remarks>Пароль в журнал НЕ пишется — только факт создания и имя входа (ТБ-043).</remarks>
    public string? AuditSummary => $"admin:account:create:{UserName}";

    /// <inheritdoc cref="CreateUserAccountCommand" />
    public sealed class Handler(IUserAccountStore accounts, IPlatformAdministration administration)
        : IRequestHandler<CreateUserAccountCommand, ResponseDto<string>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<string>> Handle(
            CreateUserAccountCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            // ИНВАРИАНТ (ТБ-012): право вести учётные записи знает ПРОФИЛЬ — пакет ролей не толкует.
            if (!await administration.CanManageAsync(cancellationToken))
            {
                return ResponseDto<string>.BadRequest(AdminGuard.Denied);
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
