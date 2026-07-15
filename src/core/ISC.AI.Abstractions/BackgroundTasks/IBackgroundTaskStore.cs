namespace ISC.AI.Abstractions.BackgroundTasks;

/// <summary>
/// Персистентный журнал статусов фоновых задач (Э4-20, §5.1.4.5): переживает перезапуск, чтобы (а)
/// пользователь видел статус; (б) осиротевшие после рестарта задачи помечались ошибочными. Сам делегат-
/// работа НЕ хранится (живёт в очереди в памяти) — восстановление именно ПОМЕЧАЕТ прерванные, не перезапускает.
/// </summary>
public interface IBackgroundTaskStore
{
    /// <summary>Создаёт запись задачи со статусом <see cref="BackgroundTaskStatus.Queued"/>.</summary>
    Task CreateAsync(Guid id, string kind, CancellationToken cancellationToken = default);

    /// <summary>Помечает задачу выполняющейся (фиксирует время старта).</summary>
    Task MarkRunningAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Помечает задачу успешно завершённой.</summary>
    Task MarkCompletedAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Помечает задачу завершившейся ошибкой с текстом ошибки.</summary>
    Task MarkFailedAsync(Guid id, string errorMessage, CancellationToken cancellationToken = default);

    /// <summary>Возвращает снимок задачи или <c>null</c>, если не найдена.</summary>
    Task<BackgroundTaskInfo?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Восстановление после перезапуска (§5.1.4.5): помечает все незавершённые задачи
    /// (<see cref="BackgroundTaskStatus.Queued"/>/<see cref="BackgroundTaskStatus.Running"/>) ошибочными —
    /// их делегаты потеряны вместе с очередью в памяти. Возвращает число помеченных.
    /// </summary>
    Task<int> RecoverOrphanedAsync(CancellationToken cancellationToken = default);
}
