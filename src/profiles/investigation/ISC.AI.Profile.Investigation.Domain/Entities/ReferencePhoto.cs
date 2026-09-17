using ISC.AI.Abstractions.Entities;
using ISC.AI.Abstractions.Security;

namespace ISC.AI.Profile.Investigation.Domain.Entities;

/// <summary>
/// Эталонное изображение фигуранта (<c>investigation.reference_photo</c>, ТФ-ПЕР-01, ТБ-077): ссылка по
/// значению на лицо/носитель в схеме <c>media</c>, источник, законное основание, дата пересмотра.
/// Замена эталона сохраняет прежний (<see cref="SupersededById"/>) — событие аудита.
/// </summary>
public class ReferencePhoto : AuditableEntity, IClassified
{
    /// <summary>Фигурант (FK внутри схемы).</summary>
    public int PersonId { get; set; }

    /// <summary>Навигация к фигуранту.</summary>
    public Person? Person { get; set; }

    /// <summary>Носитель в схеме <c>media</c> (по значению).</summary>
    public int MediaAssetId { get; set; }

    /// <summary>Лицо в схеме <c>media</c> (по значению), если выбрано конкретное.</summary>
    public int? MediaFaceId { get; set; }

    /// <summary>Оценка качества лица (ТО-мат-07), если известна.</summary>
    public float? QualityScore { get; set; }

    /// <summary>Источник изображения.</summary>
    public string? Source { get; set; }

    /// <summary>Законное основание хранения эталона (ТБ-074).</summary>
    public string? LegalBasis { get; set; }

    /// <summary>Дата пересмотра/срок хранения.</summary>
    public DateOnly? ReviewDueAt { get; set; }

    /// <summary>Кто добавил (слабая ссылка).</summary>
    public int? AddedByUserId { get; set; }

    /// <summary>Эталон, которым заменён этот (прежний сохраняется, ТБ-077).</summary>
    public int? SupersededById { get; set; }

    /// <summary>Гриф (не ниже грифа носителя, ТБ-070). NOT NULL.</summary>
    public short Classification { get; set; }

    /// <summary>Подразделение. NOT NULL.</summary>
    public int DivisionId { get; set; }
}
