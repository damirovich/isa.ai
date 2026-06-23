using Pgvector;

namespace ISC.AI.Persistence.Entities;

/// <summary>
/// Векторное представление чанка (pgvector) для семантического поиска (ТО-инф-02). Схема <c>core</c>.
/// </summary>
/// <remarks>
/// Режимные метаданные (гриф, подразделение) и флаг годности ДЕНОРМАЛИЗОВАНЫ на эту же строку:
/// фильтр доступа (ТБ-020/024) и фильтр актуальности (ADR-0013) применяются на стороне БД в строке
/// с вектором, без джойнов через чанк/документ. Это опора fail-closed-извлечения и защиты прямого
/// доступа к таблице векторов (ТБ-020, подпроверка GATE-1). Физически удаляемый носитель ДСП (ТБ-064).
/// </remarks>
public class EmbeddingEntity : AuditableEntity
{
    /// <summary>
    /// Размерность вектора выбранного эмбеддера (EmbeddingGemma, ADR-0011); фиксируется на уровне схемы
    /// (тип столбца <c>vector(N)</c>). Смена эмбеддера с иной размерностью = миграция + переиндексация.
    /// </summary>
    public const int Dimensions = 768;

    /// <summary>Чанк-источник.</summary>
    public int ChunkId { get; set; }

    /// <summary>Навигация к чанку.</summary>
    public ChunkEntity? Chunk { get; set; }

    /// <summary>Вектор (pgvector). Размерность — <see cref="Dimensions"/>.</summary>
    public Vector Embedding { get; set; } = null!;

    /// <summary>Роль/идентификатор модели векторизации (ru/ky), породившей вектор (ТО-прог-03).</summary>
    public required string ModelKey { get; set; }

    /// <summary>Гриф (денормализован на вектор для фильтра доступа, ТБ-020). NOT NULL.</summary>
    public short Classification { get; set; }

    /// <summary>Подразделение (денормализовано на вектор для фильтра доступа, ТБ-020). NOT NULL.</summary>
    public int DivisionId { get; set; }

    /// <summary>
    /// Нейтральный флаг годности источника (денормализован на вектор для фильтра актуальности по
    /// умолчанию, ADR-0013). NOT NULL, по умолчанию <c>true</c>.
    /// </summary>
    public bool IsCurrent { get; set; } = true;
}
