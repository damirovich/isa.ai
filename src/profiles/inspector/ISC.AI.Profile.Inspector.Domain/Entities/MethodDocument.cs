namespace ISC.AI.Profile.Inspector.Domain.Entities;

/// <summary>
/// Сохранённый методический документ (реестр методик, §5.2.9 / Ц-03 «институциональная память»):
/// программа проверки, чек-лист, инструкция, памятка или учебный материал, сформированные
/// grounded-конвейером и ДОРАБОТАННЫЕ человеком. Схема <c>inspector</c>.
/// </summary>
/// <remarks>
/// РЕЖИМ: документ наследует гриф результата генерации (<see cref="Classification"/> = максимум
/// грифов использованных фрагментов, ТБ-033) — выдача из реестра фильтруется по допуску субъекта
/// (гриф ≤ допуска), как в документообороте (ТБ-020). Правка текста возвращает утверждённый
/// документ в черновики: утверждение относится к конкретной редакции, не к «месту в реестре».
/// </remarks>
public class MethodDocument : AuditableEntity
{
    /// <summary>Вид документа (программа проверки / чек-лист / инструкция / памятка / учебный материал).</summary>
    public required string ArtifactKind { get; set; }

    /// <summary>Тип проверки (комплексная / целевая / контрольная / внеплановая или свой).</summary>
    public required string InspectionType { get; set; }

    /// <summary>Объект проверки (подразделение и/или направление).</summary>
    public required string Scope { get; set; }

    /// <summary>Текст документа (черновик генерации, дорабатывается человеком).</summary>
    public required string Body { get; set; }

    /// <summary>Метки грунтовки на момент сохранения (JSON-список проверок ссылок; опц.).</summary>
    public string? CitationsJson { get; set; }

    /// <summary>Все ли правовые ссылки были подтверждены грунтовкой на момент сохранения (GATE-2).</summary>
    public bool AllCitationsConfirmed { get; set; }

    /// <summary>Гриф (наследован от генерации — максимум грифов фрагментов; основа фильтра доступа).</summary>
    public short Classification { get; set; }

    /// <summary>Статус: черновик или утверждена.</summary>
    public MethodDocumentStatus Status { get; set; }

    /// <summary>Кто сохранил (слабая ссылка на реестр пользователей ядра, ТО-инф-06).</summary>
    public int? CreatedByUserId { get; set; }

    /// <summary>Кто утвердил (слабая ссылка; сбрасывается при правке текста).</summary>
    public int? ApprovedByUserId { get; set; }
}
