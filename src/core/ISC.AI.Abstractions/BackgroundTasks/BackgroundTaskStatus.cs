namespace ISC.AI.Abstractions.BackgroundTasks;

/// <summary>Статус фоновой ИИ-задачи (Э4-20, §5.1.3/§5.1.4).</summary>
public enum BackgroundTaskStatus
{
    /// <summary>Поставлена в очередь, ждёт выполнения.</summary>
    Queued = 0,

    /// <summary>Выполняется воркером.</summary>
    Running = 1,

    /// <summary>Успешно завершена.</summary>
    Completed = 2,

    /// <summary>Завершилась ошибкой (или прервана перезапуском системы).</summary>
    Failed = 3,
}
