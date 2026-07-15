using ISC.AI.Abstractions.BackgroundTasks;

namespace ISC.AI.Persistence.Entities;

/// <summary>
/// Персистентная запись ФОНОВОЙ задачи (Э4-20, §5.1.3/§5.1.4). Схема <c>core</c>. Хранит статус и
/// таймстемпы жизненного цикла — чтобы пользователь видел статус, а осиротевшие после перезапуска
/// задачи помечались ошибочными (§5.1.4.5). Делегат-работа НЕ персистится (живёт в очереди в памяти).
/// </summary>
public class BackgroundTaskEntity
{
    /// <summary>Идентификатор задачи (генерируется при постановке в очередь).</summary>
    public Guid Id { get; set; }

    /// <summary>Вид операции (генерация/индексация/анализ).</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Статус задачи.</summary>
    public BackgroundTaskStatus Status { get; set; }

    /// <summary>Время постановки в очередь (UTC).</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Время начала выполнения (UTC).</summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>Время завершения (UTC).</summary>
    public DateTime? FinishedAt { get; set; }

    /// <summary>Текст ошибки (при <see cref="BackgroundTaskStatus.Failed"/>).</summary>
    public string? Error { get; set; }
}
