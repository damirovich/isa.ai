using ISC.AI.Abstractions.BackgroundTasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ISC.AI.AI.BackgroundTasks;

/// <summary>
/// Фоновый воркер очереди задач (Э4-20): при СТАРТЕ восстанавливает осиротевшие задачи (§5.1.4.5), затем
/// в цикле исполняет задачи через <see cref="BackgroundTaskRunner"/>. Единственный читатель канала;
/// устойчив к ошибкам отдельной задачи (управляемая деградация).
/// </summary>
public sealed class BackgroundTaskWorker(
    BackgroundTaskRunner runner,
    IBackgroundTaskStore store,
    ILogger<BackgroundTaskWorker> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Восстановление после перезапуска: незавершённые задачи → Failed. БД может быть недоступна на
        // старте (хост стартует и без БД) — не роняем воркер, лишь логируем.
        try
        {
            var recovered = await store.RecoverOrphanedAsync(stoppingToken);
            if (recovered > 0)
            {
                BackgroundTaskLog.Recovered(logger, recovered);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            BackgroundTaskLog.RecoveryFailed(logger, ex);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await runner.RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // штатная остановка хоста
            }
            catch (Exception ex)
            {
                // Ошибка инфраструктуры (напр. персиста), не самой задачи: задача пропущена, воркер жив.
                // Следующий ReadAsync блокируется до новой постановки — тайт-лупа нет.
                BackgroundTaskLog.WorkerError(logger, ex);
            }
        }
    }
}
