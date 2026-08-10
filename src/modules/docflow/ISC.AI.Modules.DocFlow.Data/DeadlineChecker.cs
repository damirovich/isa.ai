using System.Globalization;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Modules.DocFlow.Domain.Enums;
using ISC.AI.Modules.DocFlow.Domain.Services;
using Microsoft.Extensions.Logging;

namespace ISC.AI.Modules.DocFlow.Data;

/// <summary>
/// Проверка сроков (ТЗ СКИД §4.2/разд. 5): уведомления о приближении срока и перевод истёкших
/// назначений в «Просрочено» системой.
/// </summary>
/// <remarks>
/// Сервис SCOPED: он работает с хранилищами, живущими запрос. Фоновая задача создаёт для каждого
/// тика свой scope и зовёт этот сервис; ручной запуск идёт из обычного scope запроса.
/// Сбой уведомления или журнала НЕ останавливает контроль сроков — пометка просрочки важнее
/// сопутствующих действий, и терять её из-за недоступного журнала нельзя (ТБ-030 не ослабляется:
/// сбой записи логируется).
/// </remarks>
public sealed class DeadlineChecker(
    IDocumentStore store,
    INotificationStore notifications,
    ISystemSettingsStore settings,
    IDocFlowClock clock,
    IAuditWriter auditWriter,
    ILogger<DeadlineChecker> logger) : IDeadlineChecker
{
    /// <inheritdoc />
    public async Task<DeadlineCheckResult> RunOnceAsync(CancellationToken cancellationToken = default)
    {
        var today = clock.Today;

        var notices = await NotifyDeadlinesAsync(today, cancellationToken);
        var marked = await store.MarkOverdueAsync(today, cancellationToken);

        if (marked.Count == 0)
        {
            return new DeadlineCheckResult(0, notices);
        }

        DeadlineCheckerLog.Marked(logger, marked.Count, today);

        foreach (var mark in marked)
        {
            try
            {
                await notifications.RaiseAsync(
                    new NotificationDraft(
                        Recipients(mark.AssigneeUserId, mark.InspectorUserId),
                        NotificationType.Overdue,
                        NotificationTemplates.Overdue,
                        new Dictionary<string, string> { ["document"] = mark.DocumentTitle },
                        mark.DocumentId,
                        mark.AssignmentId,
                        AboutDeadline: mark.Deadline),
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Уведомление — вспомогательное: его сбой не должен останавливать контроль сроков.
                DeadlineCheckerLog.NotificationFailed(logger, mark.AssignmentId, ex);
            }

            try
            {
                // Гриф записи — гриф ВЛАДЕЮЩЕГО документа (ТБ-032), не 0: пометка просрочки раскрывает
                // сам факт существования и статуса назначения режимного документа.
                await auditWriter.WriteAsync(
                    new AuditEntry(
                        AuditAction.Modify,
                        Classification: mark.Classification,
                        SubjectId: null,
                        ObjectRef: $"docflow:assignment:{mark.AssignmentId}:auto-overdue:{today:yyyy-MM-dd}",
                        DivisionId: mark.DivisionId),
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                DeadlineCheckerLog.AuditFailed(logger, mark.AssignmentId, ex);
            }
        }

        return new DeadlineCheckResult(marked.Count, notices);
    }

    /// <summary>
    /// Уведомления «срок приближается» и «срок сегодня» (разд. 5 ТЗ). Горизонт — СИСТЕМНАЯ НАСТРОЙКА
    /// (§9): эксплуатант меняет её в интерфейсе, и новое значение действует со следующей проверки.
    /// </summary>
    /// <remarks>
    /// Окно берётся ДИАПАЗОНОМ (сегодня..сегодня+горизонт), а не строгим равенством «срок = сегодня +
    /// горизонт», как в СКИД. При равенстве пропущенные сутки (перезапуск, ночной простой) теряли
    /// уведомление навсегда — назавтра условие уже ложно, догоняющей логики не было. Спама диапазон не
    /// даёт: повторы отсекает дедупликация по (назначение, вид, значение срока) в INotificationStore.
    /// </remarks>
    private async Task<int> NotifyDeadlinesAsync(DateOnly today, CancellationToken cancellationToken)
    {
        var horizonDays = (await settings.GetAsync(cancellationToken)).NotificationHorizonDays;

        var notices = await store.FindDeadlineNoticesAsync(today, today.AddDays(horizonDays), cancellationToken);
        foreach (var notice in notices)
        {
            var dueToday = notice.Deadline == today;
            var arguments = dueToday
                ? new Dictionary<string, string> { ["document"] = notice.DocumentTitle }
                : new Dictionary<string, string>
                {
                    ["document"] = notice.DocumentTitle,
                    ["days"] = (notice.Deadline.DayNumber - today.DayNumber)
                        .ToString(CultureInfo.InvariantCulture),
                    ["deadline"] = notice.Deadline.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture),
                };

            try
            {
                await notifications.RaiseAsync(
                    new NotificationDraft(
                        Recipients(notice.AssigneeUserId, notice.InspectorUserId),
                        dueToday ? NotificationType.DeadlineToday : NotificationType.DeadlineApproaching,
                        dueToday ? NotificationTemplates.DeadlineToday : NotificationTemplates.DeadlineApproaching,
                        arguments,
                        notice.DocumentId,
                        notice.AssignmentId,
                        AboutDeadline: notice.Deadline),
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                DeadlineCheckerLog.NotificationFailed(logger, notice.AssignmentId, ex);
            }
        }

        return notices.Count;
    }

    /// <summary>Получатели уведомления по назначению: исполнитель и инспектор документа.</summary>
    private static List<int> Recipients(int? assigneeUserId, int? inspectorUserId)
    {
        var recipients = new List<int>(2);
        if (assigneeUserId is { } assignee)
        {
            recipients.Add(assignee);
        }

        if (inspectorUserId is { } inspector && inspector != assigneeUserId)
        {
            recipients.Add(inspector);
        }

        return recipients;
    }
}
