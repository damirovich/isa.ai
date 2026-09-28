using ISC.AI.Abstractions.Entities;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Domain.Enums;

namespace ISC.AI.Profile.Investigation.Domain.Entities;

/// <summary>
/// Решение человека по пересечению (<c>investigation.intersection_review</c>, ТФ-ПЕР-07, ADR-0029 п. 6).
/// Строка принадлежит СВОЕМУ делу: гриф и подразделение — с фигуранта (ТБ-070), чужое дело указано только
/// идентификатором по значению (FK между делами нет — схема дела между собой не связывает). Одна строка на
/// (фигурант, вид, ключ, чужое дело): повторное решение перезаписывает её, история — в журнале аудита (ТБ-030).
/// </summary>
public class IntersectionReview : AuditableEntity, IClassified
{
    /// <summary>Фигурант своего дела (FK; удаление фигуранта или дела уносит решение).</summary>
    public int PersonId { get; set; }

    /// <summary>Навигация к фигуранту.</summary>
    public Person? Person { get; set; }

    /// <summary>Своё дело (с фигуранта).</summary>
    public int CaseId { get; set; }

    /// <summary>Вид совпавшего реквизита.</summary>
    public IntersectionKind Kind { get; set; }

    /// <summary>Нормализованный ключ совпадения (ТО-мат-11).</summary>
    public required string KeyNormalized { get; set; }

    /// <summary>Чужое дело — только по значению, без внешнего ключа.</summary>
    public int OtherCaseId { get; set; }

    /// <summary>Решение.</summary>
    public IntersectionDecision Decision { get; set; }

    /// <summary>Кто принял решение.</summary>
    public int? DecidedByUserId { get; set; }

    /// <summary>Когда принято решение (UTC).</summary>
    public DateTime DecidedAt { get; set; }

    /// <summary>Гриф (с фигуранта). NOT NULL.</summary>
    public short Classification { get; set; }

    /// <summary>Подразделение (с фигуранта). NOT NULL.</summary>
    public int DivisionId { get; set; }
}
