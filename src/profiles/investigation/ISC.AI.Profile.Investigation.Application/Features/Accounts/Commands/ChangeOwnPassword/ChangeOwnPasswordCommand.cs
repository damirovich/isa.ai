using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using Mediator;

namespace ISC.AI.Profile.Investigation.Application.Features.Accounts;

/// <summary>Сменить СВОЙ пароль. Доступно любому вошедшему — это не администрирование.</summary>
public sealed record ChangeOwnPasswordCommand(string CurrentPassword, string NewPassword)
    : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    /// <remarks>НИ ОДИН из паролей в журнал не попадает (ТБ-043) — только факт смены.</remarks>
    public string? AuditSummary => "investigation:account:change-own-password";

    /// <inheritdoc cref="ChangeOwnPasswordCommand" />
    public sealed class Handler(IUserAccountStore accounts, ISubjectProvider subjectProvider)
        : IRequestHandler<ChangeOwnPasswordCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(ChangeOwnPasswordCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (await subjectProvider.GetCurrentUserIdAsync(cancellationToken) is not { } userId)
            {
                return ResponseDto<bool>.BadRequest("Смена пароля требует аутентифицированного пользователя.");
            }

            // Текущий пароль проверяет хранилище: владение сессией не заменяет знание пароля.
            var status = await accounts.ChangeOwnPasswordAsync(
                userId, command.CurrentPassword, command.NewPassword, cancellationToken);
            return status switch
            {
                PasswordChangeStatus.Ok => ResponseDto<bool>.Ok(true, "Пароль изменён. Войдите заново с новым паролем."),
                PasswordChangeStatus.WrongCurrentPassword => ResponseDto<bool>.BadRequest("Текущий пароль указан неверно."),
                PasswordChangeStatus.SameAsCurrent => ResponseDto<bool>.BadRequest("Новый пароль совпадает с текущим."),
                PasswordChangeStatus.NoLocalPassword => ResponseDto<bool>.BadRequest(
                    "У вашей учётной записи нет локального пароля — вход выполняется через внешнюю систему."),
                _ => ResponseDto<bool>.NotFound("Учётная запись не найдена."),
            };
        }
    }
}
