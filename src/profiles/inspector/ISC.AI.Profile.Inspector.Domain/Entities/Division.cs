namespace ISC.AI.Profile.Inspector.Domain.Entities;

/// <summary>
/// Подразделение — объект контроля с иерархией ТУ→РО (§4.2). Схема <c>inspector</c>. Сопоставляется с
/// ПЛОСКИМ справочником подразделений СКИД ПО ЗНАЧЕНИЮ через <see cref="Code"/> (без FK через границу схем,
/// ТО-инф-06). Иерархию ведёт «ИнспекторAI» — в СКИД её нет.
/// </summary>
public class Division : AuditableEntity
{
    /// <summary>Наименование подразделения.</summary>
    public required string Name { get; set; }

    /// <summary>Код подразделения — ключ сопоставления с записью СКИД по значению (§4.2).</summary>
    public string? Code { get; set; }

    /// <summary>Родительское подразделение (null — верхний уровень, напр. ТУ). FK внутри схемы inspector.</summary>
    public int? ParentId { get; set; }

    /// <summary>Навигация к родителю.</summary>
    public Division? Parent { get; set; }

    /// <summary>Дочерние подразделения (вплоть до районного отдела, РО).</summary>
    public ICollection<Division> Children { get; set; } = [];
}
