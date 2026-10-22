using FluentValidation;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Admin.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.Admin.Application.Features.Sessions;

/// <summary>
/// Завершить сеанс сотрудника (ТФ-АДМ-09, ADR-0060): с обязательной причиной; открытые вкладки сеанса сразу уходят на
/// форму входа, cookie сеанса больше не принимается. Войти снова сотрудник может — чтобы не пускать совсем, учётную
/// запись отключают.
/// </summary>
/// <remarks>
/// ИНВАРИАНТЫ (ТБ-012/030). Право знает профиль (<see cref="IPlatformAdministration.CanManageSessionsAsync"/>).
/// Завершение — запись <c>Modify</c> в журнале с номером сеанса; причина хранится в строке сеанса (кто и почему).
/// Завершить можно только действующий сеанс — повтор отвечает «уже завершён» и ничего не меняет.
/// </remarks>
/// <param name="SessionId">Сеанс.</param>
/// <param name="Reason">Причина — обязательна.</param>
public sealed record TerminateUserSessionCommand(Guid SessionId, string Reason) : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <summary>Наименьшая длина причины.</summary>
    public const int MinReasonLength = 5;

    /// <summary>Наибольшая длина причины.</summary>
    public const int MaxReasonLength = 500;

    /// <summary>Ответ, если сеанса нет или он уже закрыт.</summary>
    public const string AlreadyEnded = "Сеанс уже завершён.";

    /// <summary>Ответ на попытку завершить собственный текущий сеанс.</summary>
    public const string OwnSession = "Это ваш текущий сеанс — чтобы его закрыть, нажмите «Выход».";

    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"admin:user-session:{SessionId:D}:terminate";

    /// <inheritdoc cref="TerminateUserSessionCommand" />
    public sealed class Handler(
        IUserSessionStore store,
        IUserSessionMonitor monitor,
        ISubjectProvider subjectProvider,
        IPlatformAdministration administration)
        : IRequestHandler<TerminateUserSessionCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(TerminateUserSessionCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            // Субъект нужен для записи «кто завершил»; без него — отказ, как у остальных команд пакета.
            if (await subjectProvider.GetCurrentUserIdAsync(cancellationToken) is not { } callerId)
            {
                return ResponseDto<bool>.BadRequest(AdminGuard.SessionsDenied);
            }

            if (!await administration.CanManageSessionsAsync(cancellationToken))
            {
                return ResponseDto<bool>.BadRequest(AdminGuard.SessionsDenied);
            }

            // Свой текущий сеанс — «Выходом»: завершение с экрана выбросило бы самого администратора посреди работы.
            if (await subjectProvider.GetCurrentSessionIdAsync(cancellationToken) == command.SessionId)
            {
                return ResponseDto<bool>.BadRequest(OwnSession);
            }

            if (!await store.EndAsync(
                    command.SessionId, UserSessionEndReason.Terminated, callerId, command.Reason.Trim(), cancellationToken))
            {
                return ResponseDto<bool>.NotFound(AlreadyEnded);
            }

            // Мгновенно: открытые вкладки сеанса — на форму входа, кэш состояния сеанса — сброшен.
            monitor.SignalEnded(command.SessionId);
            return ResponseDto<bool>.Ok(true);
        }
    }
}

/// <summary>Правила команды «Завершить сеанс»: номер сеанса и причина нужной длины.</summary>
public sealed class TerminateUserSessionValidator : AbstractValidator<TerminateUserSessionCommand>
{
    /// <inheritdoc cref="TerminateUserSessionValidator" />
    public TerminateUserSessionValidator()
    {
        RuleFor(c => c.SessionId).NotEmpty();
        RuleFor(c => (c.Reason ?? string.Empty).Trim())
            .MinimumLength(TerminateUserSessionCommand.MinReasonLength)
            .MaximumLength(TerminateUserSessionCommand.MaxReasonLength)
            .OverridePropertyName(nameof(TerminateUserSessionCommand.Reason))
            .WithMessage($"Укажите причину: от {TerminateUserSessionCommand.MinReasonLength} до {TerminateUserSessionCommand.MaxReasonLength} символов.");
    }
}
