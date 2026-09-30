using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Admin.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.Admin.Application.Features.Accounts;

/// <summary>Созданная учётная запись: номер (для назначения роли и допуска следующим шагом) и временный пароль.</summary>
/// <param name="UserId">Пользователь ядра.</param>
/// <param name="TemporaryPassword">Временный пароль — показывается администратору ОДИН раз, в базе только хеш.</param>
public sealed record CreatedUserAccount(int UserId, string TemporaryPassword);

/// <summary>Создать учётную запись; в ответе — номер и ВРЕМЕННЫЙ пароль (показывается один раз).</summary>
/// <param name="UserName">Имя входа.</param>
/// <param name="DisplayName">ФИО.</param>
/// <param name="Position">Должность.</param>
public sealed record CreateUserAccountCommand(string UserName, string? DisplayName, string? Position = null)
    : IRequest<ResponseDto<CreatedUserAccount>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    /// <remarks>Пароль в журнал НЕ пишется — только факт создания и имя входа (ТБ-043).</remarks>
    public string? AuditSummary => $"admin:account:create:{UserName}";

    /// <inheritdoc cref="CreateUserAccountCommand" />
    public sealed class Handler(IUserAccountStore accounts, IPlatformAdministration administration)
        : IRequestHandler<CreateUserAccountCommand, ResponseDto<CreatedUserAccount>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<CreatedUserAccount>> Handle(
            CreateUserAccountCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            // ИНВАРИАНТ (ТБ-012): право вести учётные записи знает ПРОФИЛЬ — пакет ролей не толкует.
            if (!await administration.CanManageAsync(cancellationToken))
            {
                return ResponseDto<CreatedUserAccount>.BadRequest(AdminGuard.Denied);
            }

            var temporary = TemporaryPassword.Generate();
            var userId = await accounts.CreateAsync(
                command.UserName, command.DisplayName, temporary, cancellationToken);

            if (userId is not { } id)
            {
                return ResponseDto<CreatedUserAccount>.BadRequest("Имя входа уже занято.");
            }

            // Должность — справочное поле; порт создания её не принимает, поэтому второй правкой той же учётки.
            if (!string.IsNullOrWhiteSpace(command.Position))
            {
                await accounts.UpdateProfileAsync(id, command.DisplayName, command.Position.Trim(), cancellationToken);
            }

            return ResponseDto<CreatedUserAccount>.Ok(
                new CreatedUserAccount(id, temporary), "Учётная запись создана. Передайте временный пароль лично.");
        }
    }
}
