using ISC.AI.Abstractions.BackgroundTasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ISC.AI.AI.BackgroundTasks;

/// <summary>
/// Исполнитель фоновых задач (Э4-20): извлекает задачу из очереди и выполняет её в ОТДЕЛЬНОМ scope,
/// обновляя статус. УПРАВЛЯЕМАЯ ДЕГРАДАЦИЯ (§5.1.3.3/§5.1.4.1): падение одной задачи (напр. модель
/// недоступна) НЕ роняет воркер и хост — фиксируется как <see cref="BackgroundTaskStatus.Failed"/> и
/// доступно для повтора.
/// </summary>
public sealed class BackgroundTaskRunner(
    ChannelBackgroundTaskQueue queue,
    IBackgroundTaskStore store,
    IServiceScopeFactory scopeFactory,
    ILogger<BackgroundTaskRunner> logger)
{
    /// <summary>
    /// Ждёт следующую задачу и выполняет её. Исключения САМОЙ задачи не пробрасываются (деградация);
    /// пробрасывается лишь <see cref="OperationCanceledException"/> при остановке хоста.
    /// </summary>
    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var item = await queue.Reader.ReadAsync(cancellationToken);
        await ProcessAsync(item, cancellationToken);
    }

    private async Task ProcessAsync(BackgroundWorkItem item, CancellationToken cancellationToken)
    {
        await store.MarkRunningAsync(item.Id, cancellationToken);
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            await item.Work(scope.ServiceProvider, cancellationToken);
            await store.MarkCompletedAsync(item.Id, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Остановка хоста — не ошибка задачи. Статус остаётся Running; восстановление на старте пометит.
            throw;
        }
        catch (Exception ex)
        {
            // Деградация: задача упала — фиксируем ошибку, хост и воркер живут дальше (CancellationToken.None:
            // не отменяем запись статуса из-за остановки).
            BackgroundTaskLog.TaskFailed(logger, ex, item.Kind, item.Id);
            await store.MarkFailedAsync(item.Id, ex.Message, CancellationToken.None);
        }
    }
}
