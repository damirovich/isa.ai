namespace ISC.AI.Profile.Inspector.Domain.Entities;

/// <summary>
/// Нарушение — ключевой структурный объект аналитики (Приложение §4; §5.3.2.8). Заносится ЧЕЛОВЕКОМ.
/// На нём стоят модули Архив, Риски, Мониторинг, Дашборд. Схема <c>inspector</c>. Обязательны подразделение,
/// вид и тяжесть. Связи с СКИД (документ-первоисточник, поручение, справка-проверка) — ПО ЗНАЧЕНИЮ
/// (RegNumber/id строкой), без FK через границу схем (ТО-инф-06). Признак повторности —
/// ПРОИЗВОДНЫЙ (считается по вид+подразделение во времени в аналитике риска, шаг 2), в сущности не хранится.
/// </summary>
public class Violation : AuditableEntity
{
    /// <summary>Подразделение, к которому относится нарушение (FK внутри схемы inspector). Обязательно.</summary>
    public int DivisionId { get; set; }

    /// <summary>Навигация к подразделению.</summary>
    public Division? Division { get; set; }

    /// <summary>Вид нарушения по классификатору (FK внутри схемы inspector). Обязательно.</summary>
    public int CategoryId { get; set; }

    /// <summary>Навигация к виду.</summary>
    public ViolationCategory? Category { get; set; }

    /// <summary>Тяжесть (4 уровня). Обязательно.</summary>
    public ViolationSeverity Severity { get; set; }

    /// <summary>Дата выявления.</summary>
    public DateOnly DetectedAt { get; set; }

    /// <summary>Статус устранения.</summary>
    public RemediationStatus RemediationStatus { get; set; }

    /// <summary>Первоисточник — RegNumber документа СКИД (по значению, опц.).</summary>
    public string? SourceDocRef { get; set; }

    /// <summary>Связанное поручение СКИД — id (по значению, опц.).</summary>
    public string? SourceAssignmentRef { get; set; }

    /// <summary>Справка-проверка — ссылка по значению (RegNumber, опц.).</summary>
    public string? ReferenceDocRef { get; set; }

    /// <summary>Причина (может быть ИИ-черновиком; заполняется/проверяется человеком).</summary>
    public string? Cause { get; set; }

    /// <summary>Рекомендация (может быть ИИ-черновиком; с грунтовкой и участием человека).</summary>
    public string? Recommendation { get; set; }
}
