using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.DocFlow.Application.Settings;

/// <summary>Единый текст отказа для настроек модуля.</summary>
public static class SettingsGuard
{
    /// <inheritdoc cref="SettingsGuard" />
    public const string Denied = "Изменение системных настроек доступно только Администратору.";
}

/// <summary>Текущие системные настройки модуля (§9).</summary>
/// <remarks>
/// Читать настройку вправе любой вошедший: горизонт уведомлений — не режимные данные, а параметр
/// поведения системы, и по нему ничего нельзя узнать о документах. Ограничение стоит на ЗАПИСИ.
/// </remarks>
public sealed record GetDocFlowSettingsQuery : IRequest<ResponseDto<DocFlowSettings>>
{
    /// <inheritdoc cref="GetDocFlowSettingsQuery" />
    public sealed class Handler(ISystemSettingsStore settings)
        : IRequestHandler<GetDocFlowSettingsQuery, ResponseDto<DocFlowSettings>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<DocFlowSettings>> Handle(
            GetDocFlowSettingsQuery query, CancellationToken cancellationToken) =>
            ResponseDto<DocFlowSettings>.Ok(await settings.GetAsync(cancellationToken));
    }
}

/// <summary>Запустить проверку сроков вручную, не дожидаясь расписания (§4.2/разд. 5).</summary>
/// <remarks>
/// Нужна в двух случаях: при развёртывании (чтобы не ждать час до первого тика) и при разборе жалобы
/// «почему не пришло уведомление» — иначе проверить гипотезу нечем. Повторный запуск БЕЗОПАСЕН:
/// перевод в «Просрочено» идемпотентен, а уведомления отсекает дедупликация.
/// </remarks>
public sealed record RunDeadlineCheckCommand : IRequest<ResponseDto<DeadlineCheckResult>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => "docflow:deadlines:run-check";

    /// <inheritdoc cref="RunDeadlineCheckCommand" />
    public sealed class Handler(IDeadlineChecker checker, IDocFlowAdministration administration)
        : IRequestHandler<RunDeadlineCheckCommand, ResponseDto<DeadlineCheckResult>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<DeadlineCheckResult>> Handle(
            RunDeadlineCheckCommand command, CancellationToken cancellationToken)
        {
            // Проверка рассылает уведомления всем участникам и меняет статусы — это не «посмотреть».
            if (!await administration.CanManageAsync(cancellationToken))
            {
                return ResponseDto<DeadlineCheckResult>.BadRequest(SettingsGuard.Denied);
            }

            var result = await checker.RunOnceAsync(cancellationToken);
            return ResponseDto<DeadlineCheckResult>.Ok(
                result,
                $"Проверка выполнена: переведено в «Просрочено» — {result.MarkedOverdue}, "
                + $"уведомлений о сроке — {result.DeadlineNotices}.");
        }
    }
}

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
            // (ADR-0017). Fail-closed: нет реализации порта — нет и права.
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
