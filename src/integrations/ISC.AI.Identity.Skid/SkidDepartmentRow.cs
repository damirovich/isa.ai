namespace ISC.AI.Identity.Skid;

/// <summary>
/// Строка таблицы <c>public.departments</c> БД СКИД (read-only проекция). Справочник плоский, без
/// иерархии; <see cref="Code"/> в СКИД nullable и без unique-индекса — сопоставление с подразделениями
/// профиля выполняется по значению кода с учётом возможного отсутствия (ТО-инф-06).
/// </summary>
public class SkidDepartmentRow
{
    /// <summary>Идентификатор подразделения СКИД.</summary>
    public Guid Id { get; set; }

    /// <summary>Код подразделения (например, «ГИ»); может отсутствовать.</summary>
    public string? Code { get; set; }

    /// <summary>Наименование.</summary>
    public required string Name { get; set; }

    /// <summary>Действующее ли подразделение.</summary>
    public bool IsActive { get; set; }
}
