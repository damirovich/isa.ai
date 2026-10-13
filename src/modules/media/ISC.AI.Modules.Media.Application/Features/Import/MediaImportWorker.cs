using ISC.AI.Abstractions.BackgroundTasks;
using ISC.AI.Modules.Media.Domain.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ISC.AI.Modules.Media.Application.Features.Import;

/// <summary>
/// Фоновая служба импорта архива (ADR-0041): принимает строки заданий по одной (<see cref="MediaImportRunner"/>) и
/// параллельно подаёт принятые носители в обработку малыми порциями (<see cref="MediaImportFeeder"/>).
/// </summary>
/// <remarks>
/// <para>
/// Работа начинается после полного старта хоста (<see cref="IHostApplicationLifetime.ApplicationStarted"/>): к этому
/// моменту обслуживание пакета уже перевело прерванную обработку в «ошибка» и не примет свежую подачу за остаток
/// прежнего процесса. Затем — восстановление: строки «принимается» возвращаются в очередь, носители, поданные прежним
/// процессом, — в «ждёт подачи» (их задачи пропали вместе с очередью в памяти).
/// </para>
/// <para>
/// Остановка задания оператором прерывает и файл, который принимается в этот момент: служба опрашивает состояние
/// задания, пока идёт приём, — файл в гигабайты не дочитывается после «Остановить». Сбой не из-за файла (БД, журнал)
/// повторяется позже, а после нескольких сбоев подряд на одной строке строка отклоняется с причиной: одна строка не
/// держит задание бесконечно.
/// </para>
/// </remarks>
public sealed partial class MediaImportWorker(
    IServiceScopeFactory scopeFactory,
    IHostApplicationLifetime lifetime,
    ILogger<MediaImportWorker> logger) : BackgroundService
{
    /// <summary>Пауза, когда работы нет.</summary>
    public static readonly TimeSpan IdleDelay = TimeSpan.FromSeconds(5);

    /// <summary>Как часто проверяется завершение обработки и подаются следующие носители.</summary>
    public static readonly TimeSpan FeedInterval = TimeSpan.FromSeconds(5);

    /// <summary>Как часто во время приёма файла проверяется, не остановлено ли задание.</summary>
    public static readonly TimeSpan StopPollInterval = TimeSpan.FromSeconds(2);

    /// <summary>Пауза после сбоя (БД недоступна и т.п.).</summary>
    public static readonly TimeSpan FailureDelay = TimeSpan.FromSeconds(30);

    /// <summary>После стольких сбоев подряд на одной строке она отклоняется.</summary>
    public const int MaxItemFailures = 3;

    private readonly Dictionary<int, int> _failures = [];

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!await WaitForStartAsync(stoppingToken))
        {
            return;
        }

        await RecoverAsync(stoppingToken);
        await Task.WhenAll(CopyLoopAsync(stoppingToken), FeedLoopAsync(stoppingToken));
    }

    private async Task<bool> WaitForStartAsync(CancellationToken stoppingToken)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var onStarted = lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        using var onStopping = stoppingToken.Register(() => started.TrySetCanceled(stoppingToken));
        try
        {
            await started.Task;
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task RecoverAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var imports = scope.ServiceProvider.GetRequiredService<IMediaImportStore>();
            var tasks = scope.ServiceProvider.GetRequiredService<IBackgroundTaskStore>();
            var items = await imports.RecoverInterruptedAsync(stoppingToken);
            var processing = await imports.RecoverProcessingAsync(
                async id => (await tasks.GetAsync(id, stoppingToken))?.Status == BackgroundTaskStatus.Completed,
                stoppingToken);
            if (items > 0 || processing > 0)
            {
                Log.Recovered(logger, items, processing);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // БД недоступна на старте: задания продолжатся, когда она появится (строки «принимается» — до следующего старта).
            Log.RecoveryFailed(logger, exception);
        }
    }

    private async Task CopyLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeSpan.Zero;
            try
            {
                delay = await StepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (MediaImportItemException exception) when (exception.Work is { } work)
            {
                delay = FailureDelay;
                await HandleItemFailureAsync(work, exception, stoppingToken);
            }
            catch (Exception exception)
            {
                Log.StepFailed(logger, exception);
                delay = FailureDelay;
            }

            if (delay > TimeSpan.Zero && !await DelayAsync(delay, stoppingToken))
            {
                break;
            }
        }
    }

    private async Task<TimeSpan> StepAsync(CancellationToken stoppingToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var runner = scope.ServiceProvider.GetRequiredService<MediaImportRunner>();
        var context = await runner.PrepareAsync(stoppingToken);
        if (context is null)
        {
            return IdleDelay;
        }

        using var item = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var imports = scope.ServiceProvider.GetRequiredService<IMediaImportStore>();
        var watch = WatchJobAsync(imports, context.Job.JobId, item);
        try
        {
            var step = await runner.RunItemAsync(context, item.Token);
            if (step == MediaImportStep.JobCompleted)
            {
                Log.JobCompleted(logger, context.Job.JobId);
            }
        }
        finally
        {
            await item.CancelAsync();
            await watch;
        }

        return TimeSpan.Zero;
    }

    // Пока принимается файл, раз в StopPollInterval проверяет состояние задания: остановлено — приём прерывается.
    private static async Task WatchJobAsync(IMediaImportStore imports, int jobId, CancellationTokenSource item)
    {
        try
        {
            while (!item.IsCancellationRequested)
            {
                await Task.Delay(StopPollInterval, item.Token);
                var status = await imports.GetJobStatusAsync(jobId, item.Token);
                if (status != Domain.Model.MediaImportJobStatus.Running)
                {
                    await item.CancelAsync();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Приём файла завершился раньше — наблюдение больше не нужно.
        }
        catch (Exception)
        {
            // Сбой опроса не прерывает приём: остановку увидит следующий шаг.
        }
    }

    private async Task HandleItemFailureAsync(Domain.Model.MediaImportWorkItem work, Exception exception, CancellationToken stoppingToken)
    {
        var failures = _failures.GetValueOrDefault(work.ItemId) + 1;
        _failures[work.ItemId] = failures;
        Log.ItemFailed(logger, exception.InnerException ?? exception, work.JobId, work.Draft.RowNumber, failures);
        if (failures < MaxItemFailures)
        {
            return;
        }

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var runner = scope.ServiceProvider.GetRequiredService<MediaImportRunner>();
            if (await runner.PrepareAsync(stoppingToken) is { } context && context.Job.JobId == work.JobId)
            {
                await runner.RejectAsync(
                    context, work,
                    $"сбой при приёме ({MaxItemFailures} раза подряд): {(exception.InnerException ?? exception).Message}",
                    stoppingToken);
                _failures.Remove(work.ItemId);
            }
        }
        catch (Exception rejectFailure) when (rejectFailure is not OperationCanceledException)
        {
            Log.StepFailed(logger, rejectFailure);
        }
    }

    private async Task FeedLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<MediaImportFeeder>().FeedAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                Log.FeedFailed(logger, exception);
            }

            if (!await DelayAsync(FeedInterval, stoppingToken))
            {
                break;
            }
        }
    }

    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(delay, stoppingToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Information,
            Message = "Импорт архива: после перезапуска возвращено в очередь строк — {Items}, носителей на повторную подачу в обработку — {Processing}.")]
        public static partial void Recovered(ILogger logger, int items, int processing);

        [LoggerMessage(Level = LogLevel.Warning,
            Message = "Импорт архива: восстановление после перезапуска не выполнено — задания продолжатся, когда будет доступна БД.")]
        public static partial void RecoveryFailed(ILogger logger, Exception exception);

        [LoggerMessage(Level = LogLevel.Information, Message = "Импорт архива: задание {JobId} завершено — все строки разобраны.")]
        public static partial void JobCompleted(ILogger logger, int jobId);

        [LoggerMessage(Level = LogLevel.Error, Message = "Импорт архива: сбой шага — повтор через 30 с.")]
        public static partial void StepFailed(ILogger logger, Exception exception);

        [LoggerMessage(Level = LogLevel.Error,
            Message = "Импорт архива: сбой при приёме строки {RowNumber} задания {JobId} (подряд — {Failures}).")]
        public static partial void ItemFailed(ILogger logger, Exception exception, int jobId, int rowNumber, int failures);

        [LoggerMessage(Level = LogLevel.Error, Message = "Импорт архива: сбой подачи носителей в обработку — повтор через 5 с.")]
        public static partial void FeedFailed(ILogger logger, Exception exception);
    }
}
