using System;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Profile.Inspector.Domain.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ISC.AI.Profile.Inspector.Data;

/// <summary>
/// Фоновая проверка контрольных сроков устранения нарушений (ТФ-МОН-01): неустранённые нарушения
/// с истёкшим сроком СИСТЕМА переводит в «Просрочено» — светофор риска и мониторинг не зависят от
/// того, вспомнил ли человек сменить статус. Интервал — <c>Inspector:RemediationCheckIntervalMinutes</c>
/// (по умолчанию 60, как у проверки сроков поручений документооборота).
/// </summary>
/// <remarks>
/// Перенос механики <c>DeadlineCheckerJob</c> docflow: ошибка тика не валит задачу — лог и следующий
/// тик; аудит каждого перевода — после успешной записи, с перехватом (сбой журнала не останавливает
/// контроль сроков: это фоновая пометка, не выдача данных — ТБ-030 не ослабляется).
/// </remarks>
public sealed class RemediationDeadlineJob(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<RemediationDeadlineJob> logger) : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(
            int.TryParse(configuration["Inspector:RemediationCheckIntervalMinutes"], out var minutes) && minutes > 0
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
                RemediationDeadlineLog.TickFailed(logger, ex);
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

    /// <summary>Один тик: пометка просроченных + аудит каждого перевода (системное действие, без субъекта).</summary>
    private async Task RunTickAsync(CancellationToken cancellationToken)
    {
        // Свой scope на каждый тик: хранилища scoped, а фоновая задача живёт всё время работы хоста.
        using var scope = scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IViolationStore>();
        var auditWriter = scope.ServiceProvider.GetRequiredService<IAuditWriter>();

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var marked = await store.MarkOverdueAsync(today, cancellationToken);
        if (marked.Count == 0)
        {
            return;
        }

        RemediationDeadlineLog.Marked(logger, marked.Count, today);

        foreach (var mark in marked)
        {
            try
            {
                // Нарушения грифа не несут (реестр читают все вошедшие) — Classification 0;
                // подразделение пишем: по нему пометка видна в разрезах журнала.
                await auditWriter.WriteAsync(
                    new AuditEntry(
                        AuditAction.Modify,
                        Classification: 0,
                        SubjectId: null,
                        ObjectRef: $"inspector:violation:{mark.ViolationId}:auto-overdue:{today:yyyy-MM-dd}",
                        DivisionId: mark.DivisionId),
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                RemediationDeadlineLog.AuditFailed(logger, mark.ViolationId, ex);
            }
        }
    }
}

/// <summary>Строго-типизированные лог-сообщения проверки сроков устранения (LoggerMessage — CA1848).</summary>
internal static partial class RemediationDeadlineLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Сроки устранения: тик завершился ошибкой — продолжаем по расписанию")]
    public static partial void TickFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Сроки устранения: переведено в «Просрочено» {Count} нарушений (на {Today})")]
    public static partial void Marked(ILogger logger, int count, DateOnly today);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Сроки устранения: аудит перевода нарушения {ViolationId} не записан — пометка выполнена")]
    public static partial void AuditFailed(ILogger logger, int violationId, Exception exception);
}
