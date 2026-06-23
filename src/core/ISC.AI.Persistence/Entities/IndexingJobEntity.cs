namespace ISC.AI.Persistence.Entities;

/// <summary>
/// Задание фонового конвейера загрузки/индексации (ingestion — Э3-07/Э4-01). Схема <c>core</c>.
/// Идемпотентность повторного запуска — ТНД-002.
/// </summary>
public class IndexingJobEntity : AuditableEntity
{
    /// <summary>Документ, который индексируется (если применимо).</summary>
    public int? DocumentId { get; set; }

    /// <summary>Статус задания.</summary>
    public IndexingJobStatus Status { get; set; }

    /// <summary>Время завершения.</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>Текст ошибки (при <see cref="IndexingJobStatus.Failed"/>).</summary>
    public string? Error { get; set; }
}
