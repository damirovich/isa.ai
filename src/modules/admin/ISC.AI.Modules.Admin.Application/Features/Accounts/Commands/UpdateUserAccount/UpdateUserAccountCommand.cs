using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Admin.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.Admin.Application.Features.Accounts;

/// <summary>Изменить справочные поля учётной записи: ФИО и должность.</summary>
/// <remarks>
/// Роль и допуск здесь НЕ меняются — у них свои экраны и свои записи в журнале. Общая форма
/// «поменять всё сразу» склеивает разные полномочия в одно действие: администратор, исправивший
/// опечатку в фамилии, незаметно для себя переутверждает и права.
/// </remarks>
public sealed record UpdateUserAccountCommand(int UserId, string? DisplayName, string? Position)
    : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"admin:account:{UserId}:update-profile";

    /// <inheritdoc cref="UpdateUserAccountCommand" />
    public sealed class Handler(IUserAccountStore accounts, IPlatformAdministration administration)
        : IRequestHandler<UpdateUserAccountCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(
            UpdateUserAccountCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            // ИНВАРИАНТ (ТБ-012): правка чужой учётной записи — администрирование, право даёт профиль.
            if (!await administration.CanManageAsync(cancellationToken))
            {
                return ResponseDto<bool>.BadRequest(AdminGuard.Denied);
            }

            return await accounts.UpdateProfileAsync(
                command.UserId, command.DisplayName, command.Position, cancellationToken)
                ? ResponseDto<bool>.Ok(true, "Сохранено.")
                : ResponseDto<bool>.NotFound("Учётная запись не найдена.");
        }
    }
}
