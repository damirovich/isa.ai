using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.DocFlow.Application.Features.Settings;

/// <summary>Задать горизонт уведомлений «срок приближается» (§9, разд. 5).</summary>
public sealed record SetNotificationHorizonCommand(int Days)
    : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"docflow:settings:notification-horizon:{Days}";

    /// <inheritdoc cref="SetNotificationHorizonCommand" />
    public sealed class Handler(
        ISystemSettingsStore settings, IDocFlowAdministration administration, ISubjectProvider subjectProvider)
        : IRequestHandler<SetNotificationHorizonCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(
            SetNotificationHorizonCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            // Право — через нейтральный порт: роли ведёт ПРОФИЛЬ, модуль на него не ссылается
            // (ADR-0017). Реализацию обязан дать профиль: без неё приложение не запустится —
            // отсутствие правила о доступе должно быть заметно сразу (см. DocFlowModule.RequiredServices).
            if (!await administration.CanManageAsync(cancellationToken))
            {
                return ResponseDto<bool>.BadRequest(SettingsGuard.Denied);
            }

            var changedBy = await subjectProvider.GetCurrentUserIdAsync(cancellationToken);
            if (!await settings.SetNotificationHorizonAsync(command.Days, changedBy, cancellationToken))
            {
                return ResponseDto<bool>.BadRequest(
                    $"Горизонт уведомлений должен быть от {DocFlowSettings.MinHorizonDays} "
                    + $"до {DocFlowSettings.MaxHorizonDays} дней.");
            }

            return ResponseDto<bool>.Ok(true, "Настройка сохранена и действует со следующей проверки сроков.");
        }
    }
}
