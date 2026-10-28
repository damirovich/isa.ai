using ISC.AI.Backup;
using ISC.AI.Persistence.Backup;

namespace ISC.AI.Web.Common;

/// <summary>
/// Исполнитель резервных копий (ТФ-АДМ-04, ADR-0063) — отдельный от общей фоновой очереди: копия идёт часами и не
/// должна задерживать обработку носителей. При старте хоста: прогоны «идёт» — «прервана»; отметки о восстановлении из
/// хранилища — в журнал аудита (запись о восстановлении пишет хост, восстановленная база её не содержит). Дальше — задания
/// по одному, каждое в своей области DI. Всё время работы держит отметку «система запущена» (<see cref="HostPresence"/>):
/// программа восстановления по ней не восстановит «на живую».
/// </summary>
public sealed partial class BackupJobWorker(
    BackupJobChannel jobs,
    BackupSettings settings,
    IServiceScopeFactory scopes,
    ILogger<BackupJobWorker> logger) : BackgroundService
{
    private FileStream? _presence;

    /// <inheritdoc />
    public override void Dispose()
    {
        _presence?.Dispose();
        base.Dispose();
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _presence = HostPresence.TryHold(settings.StorageRoot);
            if (_presence is null)
            {
                LogPresenceBusy(logger, settings.StorageRoot);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LogPresenceFailed(logger, exception, settings.StorageRoot);
        }

        // База может быть недоступна на старте (хост поднимается и без неё) — не роняем исполнитель, только журнал.
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<BackupService>();
            var recovered = await service.RecoverAfterRestartAsync(stoppingToken);
            var restores = await service.ProcessRestoreMarkersAsync(stoppingToken);
            if (recovered + restores > 0)
            {
                LogStartup(logger, recovered, restores);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogStartupFailed(logger, exception);
        }

        try
        {
            await foreach (var job in jobs.ReadAllAsync(stoppingToken))
            {
                await using var scope = scopes.CreateAsyncScope();
                var service = scope.ServiceProvider.GetRequiredService<BackupService>();
                try
                {
                    if (job.Kind == BackupJobKind.Copy)
                    {
                        await service.RunCopyAsync(job.RunId, job.UserId, stoppingToken);
                    }
                    else
                    {
                        await service.RunVerifyAsync(job.RunId, job.UserId, stoppingToken);
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // Сбой записи итога (база недоступна): прогон останется «идёт» и станет «прерван» при перезапуске.
                    LogJobFailed(logger, exception, job.RunId);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Штатная остановка хоста.
        }
    }

    [LoggerMessage(EventId = 6311, Level = LogLevel.Information, Message = "Резервные копии при старте: прервано {Recovered}, записано восстановлений {Restores}")]
    private static partial void LogStartup(ILogger logger, int recovered, int restores);

    [LoggerMessage(EventId = 6312, Level = LogLevel.Warning, Message = "Резервные копии: проверка при старте не выполнена")]
    private static partial void LogStartupFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 6313, Level = LogLevel.Error, Message = "Резервная копия {RunId}: исполнитель не записал итог")]
    private static partial void LogJobFailed(ILogger logger, Exception exception, int runId);

    [LoggerMessage(EventId = 6314, Level = LogLevel.Warning, Message = "Отметку «система запущена» в {StorageRoot} держит другой процесс — второй хост на том же хранилище?")]
    private static partial void LogPresenceBusy(ILogger logger, string storageRoot);

    [LoggerMessage(EventId = 6315, Level = LogLevel.Warning, Message = "Отметку «система запущена» в {StorageRoot} не удалось создать")]
    private static partial void LogPresenceFailed(ILogger logger, Exception exception, string storageRoot);
}
