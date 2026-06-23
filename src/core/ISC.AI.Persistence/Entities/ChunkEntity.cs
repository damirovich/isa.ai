namespace ISC.AI.Persistence.Entities;

/// <summary>
/// Фрагмент документа — единица индексации и извлечения для RAG. Схема <c>core</c>.
/// </summary>
/// <remarks>
/// Гриф и подразделение ДЕНОРМАЛИЗОВАНЫ на чанк: фильтр доступа применяется на стороне БД (ТБ-020)
/// в той же строке, где лежит вектор, без джойнов. Поля NOT NULL — опора fail-closed (ТБ-021/024).
/// Вектор (pgvector) добавляется на Э3-02. Физически удаляемый носитель ДСП (ТБ-064).
/// </remarks>
public class ChunkEntity : AuditableEntity
{
    /// <summary>Родительский документ.</summary>
    public int DocumentId { get; set; }

    /// <summary>Навигация к документу.</summary>
    public DocumentEntity? Document { get; set; }

    /// <summary>Порядковый номер фрагмента в документе.</summary>
    public int Ordinal { get; set; }

    /// <summary>Текст фрагмента.</summary>
    public required string Text { get; set; }

    /// <summary>Гриф (денормализован для фильтра доступа, ТБ-020). NOT NULL.</summary>
    public short Classification { get; set; }

    /// <summary>Подразделение (денормализовано для фильтра доступа, ТБ-020). NOT NULL.</summary>
    public int DivisionId { get; set; }

    /// <summary>
    /// Нейтральный флаг годности источника: фрагмент актуален (не утратил силу). По умолчанию <c>true</c>.
    /// Ядро фильтрует по нему в <c>IRetriever</c> и проверяет в грунтовке (ADR-0013); доменный смысл
    /// «актуальности» (для НПА — действующая редакция) материализует профиль (ТО-инф-04). NOT NULL.
    /// </summary>
    public bool IsCurrent { get; set; } = true;
}
