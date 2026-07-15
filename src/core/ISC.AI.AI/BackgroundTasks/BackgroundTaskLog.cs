using Microsoft.Extensions.Logging;

namespace ISC.AI.AI.BackgroundTasks;

/// <summary>Строго-типизированные лог-сообщения очереди фоновых задач (LoggerMessage — без боксинга, CA1848).</summary>
internal static partial class BackgroundTaskLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Фоновая задача {Kind} ({Id}) завершилась ошибкой")]
    public static partial void TaskFailed(ILogger logger, Exception exception, string kind, Guid id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Восстановление очереди: {Count} прерванных задач помечены ошибочными")]
    public static partial void Recovered(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Не удалось восстановить осиротевшие задачи при старте")]
    public static partial void RecoveryFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Ошибка воркера фоновых задач (задача пропущена, воркер продолжает)")]
    public static partial void WorkerError(ILogger logger, Exception exception);
}
