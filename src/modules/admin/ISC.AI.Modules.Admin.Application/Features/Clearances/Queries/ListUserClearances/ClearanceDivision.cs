namespace ISC.AI.Modules.Admin.Application.Features.Clearances;

/// <summary>
/// Подразделение из допуска: наименование либо признак «в справочнике профиля такого номера нет».
/// </summary>
/// <param name="Id">Номер подразделения — тот, что лежит в <c>core.clearance.division_scope</c>.</param>
/// <param name="Name">
/// Наименование из справочника профиля; <see langword="null"/> — номера в справочнике нет.
/// </param>
/// <remarks>
/// Ведение допусков (ТБ-011/020/021) из интерфейса: допуск — ядровое понятие (<c>core.clearance</c>),
/// а справочник подразделений ведёт профиль (порт <c>IDivisionCatalog</c>), поэтому наименование
/// может и не найтись. До этого экрана гриф и подразделения правились ТОЛЬКО SQL'ем по живой базе:
/// правка не попадала в неизменяемый журнал (ТБ-030), а расхождение номеров со справочником
/// проявлялось у пользователя как «не могу зарегистрировать документ, хотя допуск выдан».
/// </remarks>
/// <param name="ParentId">Вышестоящее подразделение — для дерева на экране допуска.</param>
/// <param name="OwnName">Собственное наименование без вышестоящего — для строки дерева.</param>
/// <param name="Badge">Короткая метка профиля (у «Следствия» — отдел ОН/ОУ).</param>
/// <param name="IsActive">Действующее ли подразделение.</param>
public sealed record ClearanceDivision(
    int Id, string? Name, int? ParentId = null, string? OwnName = null, string? Badge = null, bool IsActive = true)
{
    /// <summary>Есть ли такое подразделение в справочнике профиля.</summary>
    public bool IsKnown => Name is not null;
}
