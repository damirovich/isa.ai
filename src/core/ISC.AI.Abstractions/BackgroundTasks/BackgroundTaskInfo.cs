namespace ISC.AI.Abstractions.BackgroundTasks;

/// <summary>Снимок состояния фоновой задачи для отображения пользователю (Э4-20, §5.1.3.1).</summary>
/// <param name="Id">Идентификатор задачи.</param>
/// <param name="Kind">Вид операции (напр. «генерация справки», «индексация корпуса»).</param>
/// <param name="Status">Текущий статус.</param>
/// <param name="CreatedAt">Время постановки в очередь (UTC).</param>
/// <param name="StartedAt">Время начала выполнения (UTC), если началась.</param>
/// <param name="FinishedAt">Время завершения (UTC), если завершилась.</param>
/// <param name="Error">Текст ошибки при <see cref="BackgroundTaskStatus.Failed"/>.</param>
public sealed record BackgroundTaskInfo(
    Guid Id,
    string Kind,
    BackgroundTaskStatus Status,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? FinishedAt,
    string? Error);
