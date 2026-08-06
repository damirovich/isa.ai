using System.Globalization;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Modules.DocFlow.Domain.Enums;
using ISC.AI.Modules.DocFlow.Domain.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ISC.AI.Modules.DocFlow.Data;

/// <summary>
/// Фоновая проверка сроков назначений (ТЗ СКИД §4.2/§5, перенос <c>DeadlineCheckerJob</c> СКИД):
/// назначения с истёкшим сроком переводятся в «Просрочено» системой. Интервал —
/// <c>DocFlow:DeadlineCheckIntervalMinutes</c> (по умолчанию 60, как в СКИД).
/// </summary>
/// <remarks>
/// Перенесена только ветка Overdue; уведомления «за N дней» и «сегодня» (разд. 5 ТЗ) подключаются
/// вместе с сущностью уведомлений (этап 2.2 Э4-35). Ошибка тика не валит задачу — лог и следующий тик.
/// Аудит перевода — после успешной записи, с перехватом: сбой журнала не должен останавливать
/// контроль сроков (это фоновая пометка, не выдача данных — ТБ-030 не ослабляется).
/// </remarks>
public sealed class DeadlineCheckerJob(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<DeadlineCheckerJob> logger) : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);

    /// <summary>Горизонт уведомления «срок приближается», дней — дефолт СКИД (разд. 5 ТЗ).</summary>
    private const int DefaultNotificationHorizonDays = 7;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(
            int.TryParse(configuration["DocFlow:DeadlineCheckIntervalMinutes"], out var minutes) && minutes > 0
                ? minutes
                : 60);

        try
        {
            // Пауза старта: дождаться полной инициализации DI и применения миграций.
            await Task.Delay(StartupDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunTickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                DeadlineCheckerLog.TickFailed(logger, ex);
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Один тик: уведомления о приближении срока и о сроке сегодня (разд. 5), затем пометка просроченных
    /// с аудитом каждого перевода (системное действие, без субъекта) и уведомлением о просрочке.
    /// </summary>
    private async Task RunTickAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IDocumentStore>();
        var clock = scope.ServiceProvider.GetRequiredService<IDocFlowClock>();
        var auditWriter = scope.ServiceProvider.GetRequiredService<IAuditWriter>();
        var notifications = scope.ServiceProvider.GetRequiredService<INotificationStore>();

        var today = clock.Today;

        await NotifyDeadlinesAsync(store, notifications, today, cancellationToken);

        var marked = await store.MarkOverdueAsync(today, cancellationToken);
        if (marked.Count == 0)
        {
            return;
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
    }

    /// <summary>
    /// Уведомления «срок приближается» и «срок сегодня» (разд. 5 ТЗ). Горизонт — конфигурация
    /// <c>DocFlow:NotificationHorizonDays</c> (по умолчанию 7, как дефолт СКИД).
    /// </summary>
    /// <remarks>
    /// Окно берётся ДИАПАЗОНОМ (сегодня..сегодня+горизонт), а не строгим равенством «срок = сегодня +
    /// горизонт», как в СКИД. При равенстве пропущенные сутки (перезапуск, ночной простой) теряли
    /// уведомление навсегда — назавтра условие уже ложно, догоняющей логики не было. Спама диапазон не
    /// даёт: повторы отсекает дедупликация по (назначение, вид, значение срока) в INotificationStore.
    /// </remarks>
    private async Task NotifyDeadlinesAsync(
        IDocumentStore store, INotificationStore notifications, DateOnly today, CancellationToken cancellationToken)
    {
        var horizonDays = int.TryParse(configuration["DocFlow:NotificationHorizonDays"], out var configured)
            && configured is > 0 and <= 30
                ? configured
                : DefaultNotificationHorizonDays;

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
    }

    /// <summary>Получатели уведомления по назначению: исполнитель и инспектор документа (см. DeadlineNotice).</summary>
    private static List<int> Recipients(int? assigneeUserId, int? inspectorUserId)
    {
        var recipients = new List<int>(2);
        if (assigneeUserId is { } assignee)
        {
            recipients.Add(assignee);
        }

        if (inspectorUserId is { } inspector)
        {
            recipients.Add(inspector);
        }

        return recipients;
    }
}

/// <summary>Строго-типизированные лог-сообщения проверки сроков (LoggerMessage — CA1848).</summary>
internal static partial class DeadlineCheckerLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Проверка сроков: тик завершился ошибкой — продолжаем по расписанию")]
    public static partial void TickFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Проверка сроков: переведено в «Просрочено» {Count} назначений (на {Today})")]
    public static partial void Marked(ILogger logger, int count, DateOnly today);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Проверка сроков: аудит перевода назначения {AssignmentId} не записан — пометка выполнена, запись повторится следующим событием")]
    public static partial void AuditFailed(ILogger logger, int assignmentId, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Проверка сроков: уведомление по назначению {AssignmentId} не создано — контроль сроков не прерван")]
    public static partial void NotificationFailed(ILogger logger, int assignmentId, Exception exception);
}
