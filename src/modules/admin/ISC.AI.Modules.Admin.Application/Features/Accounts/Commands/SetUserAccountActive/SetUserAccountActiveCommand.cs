using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Admin.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.Admin.Application.Features.Accounts;

/// <summary>Включить или отключить учётную запись (отключение обрывает сессии немедленно).</summary>
public sealed record SetUserAccountActiveCommand(int UserId, bool IsActive, string? Reason = null)
    : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary =>
        $"admin:account:{UserId}:{(IsActive ? "enable" : "disable")}";

    /// <inheritdoc cref="SetUserAccountActiveCommand" />
    public sealed class Handler(
        IUserAccountStore accounts,
        ISubjectProvider subjectProvider,
        IPlatformAdministration administration)
        : IRequestHandler<SetUserAccountActiveCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(
            SetUserAccountActiveCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            // Субъект нужен НЕ ДЛЯ ПРАВА (его даёт профиль), а для проверки «не сам себя» ниже.
            if (await subjectProvider.GetCurrentUserIdAsync(cancellationToken) is not { } callerId)
            {
                return ResponseDto<bool>.BadRequest(AdminGuard.Denied);
            }

            // ИНВАРИАНТ (ТБ-012): право отключать учётные записи знает профиль.
            if (!await administration.CanManageAsync(cancellationToken))
            {
                return ResponseDto<bool>.BadRequest(AdminGuard.Denied);
            }

            // Отключить САМОГО СЕБЯ нельзя: администратор мгновенно потерял бы доступ и, если он
            // единственный, систему стало бы некому администрировать (тот же класс отказа, что
            // «замок без ключа» режима первичной настройки).
            if (command.UserId == callerId && !command.IsActive)
            {
                return ResponseDto<bool>.BadRequest(
                    "Нельзя отключить собственную учётную запись — вы потеряете доступ немедленно.");
            }

            return await accounts.SetActiveAsync(command.UserId, command.IsActive, command.Reason, cancellationToken)
                ? ResponseDto<bool>.Ok(true)
                : ResponseDto<bool>.NotFound("Учётная запись не найдена.");
        }
    }
}
