namespace ISC.AI.Abstractions.Entities;

/// <summary>
/// Контракт сущности с автоматическим аудитом дат создания и последнего изменения.
/// Перехватчик <c>SaveChangesAsync</c> обнаруживает реализации через <c>ChangeTracker</c> и
/// проставляет <see cref="CreatedAt"/> (только при Added) и <see cref="UpdatedAt"/> (при Added/Modified).
/// </summary>
public interface IAuditableEntity
{
    /// <summary>Дата создания (UTC).</summary>
    DateTime CreatedAt { get; set; }

    /// <summary>Дата последнего изменения (UTC).</summary>
    DateTime? UpdatedAt { get; set; }
}
