namespace ISC.AI.Persistence.Entities;

/// <summary>
/// Документ-первоисточник корпуса — доменно-нейтральная единица (скан, файл, выгрузка; для профиля
/// «ИнспекторAI» — НПА). Схема <c>core</c>. Несёт обязательные режимные метаданные — гриф и
/// подразделение (ТО-инф-03, ТБ-002). Аудируемый и физически удаляемый (гарантированное удаление
/// ДСП — ТБ-064), поэтому НЕ <c>ISoftDeletable</c>. Доменную связь с нормой НПА ведёт профиль
/// (<c>inspector.norm_document_link</c>, слабая ссылка по <c>Id</c> без FK через границу схем, ТО-инф-06).
/// </summary>
public class DocumentEntity : AuditableEntity
{
    /// <summary>Тип документа (закон, приказ, справка, акт и т. п.).</summary>
    public required string DocType { get; set; }

    /// <summary>Наименование документа.</summary>
    public required string Title { get; set; }

    /// <summary>Источник/происхождение.</summary>
    public string? Source { get; set; }

    /// <summary>Дата документа.</summary>
    public DateOnly? DocDate { get; set; }

    /// <summary>Гриф (уровень: выше — строже). NOT NULL — основа фильтра доступа (ТБ-020/024).</summary>
    public short Classification { get; set; }

    /// <summary>Подразделение-владелец. NOT NULL. Логическая ссылка без FK через границу схем (ТО-инф-06).</summary>
    public int DivisionId { get; set; }

    /// <summary>Ссылка на исходный файл в защищённом хранилище.</summary>
    public string? StorageUri { get; set; }

    /// <summary>Хеш содержимого для дедупликации (ТНД-002, ТО-инф-07).</summary>
    public byte[]? ContentHash { get; set; }

    /// <summary>Фрагменты документа.</summary>
    public ICollection<ChunkEntity> Chunks { get; set; } = [];
}
