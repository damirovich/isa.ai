namespace ISC.AI.Abstractions.Entities;

/// <summary>
/// Базовый класс всех доменных сущностей платформы: единый первичный ключ и базовые метки времени.
/// </summary>
/// <remarks>
/// Размещён в <c>Abstractions</c> (только базовые типы, без EF/Npgsql) — наследуется и ядром
/// (схема <c>core</c>), и профилем (схема <c>inspector</c>), чтобы аудит и мягкое удаление были
/// единообразны во всём решении.
/// </remarks>
public abstract class BaseEntity
{
    /// <summary>Первичный ключ (identity).</summary>
    public int Id { get; set; }

    /// <summary>Дата создания (UTC). Для аудируемых сущностей перезаписывается в <c>SaveChangesAsync</c>.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Дата последнего изменения (UTC).</summary>
    public DateTime? UpdatedAt { get; set; }

    /// <summary>Инициализирует <see cref="CreatedAt"/> текущим UTC.</summary>
    protected BaseEntity() => CreatedAt = DateTime.UtcNow;
}
