namespace ISC.AI.Profile.Inspector.Domain.Services;

// Контракты справочника подразделений (§4.2). Разрез «интерфейс отдельно, контракты по агрегату» —
// та же компоновка, что DocumentContracts.cs в модуле документооборота (2026-08-10).

/// <summary>
/// Узел справочника подразделений (иерархия ТУ→РО, §4.2): плоская строка с родителем.
/// </summary>
/// <param name="IsActive">Действующее ли подразделение (неактивное не предлагается при регистрации).</param>
/// <param name="Users">Сколько пользователей имеют это подразделение в допуске.</param>
/// <param name="Documents">Сколько документов принадлежит подразделению.</param>
/// <param name="Assignments">Сколько поручений выдано на подразделение.</param>
/// <param name="Children">Сколько дочерних подразделений.</param>
/// <param name="Kind">Тип: территориальное/линейное (§4.2; разрез аналитики ТФ-АРХ-03).</param>
public sealed record DivisionNode(
    int Id,
    string Name,
    string? Code,
    int? ParentId,
    bool IsActive = true,
    int Users = 0,
    int Documents = 0,
    int Assignments = 0,
    int Children = 0,
    Enums.DivisionKind Kind = Enums.DivisionKind.Territorial)
{
    /// <summary>
    /// Можно ли удалить: за подразделением не числится ничего и у него нет потомков.
    /// </summary>
    /// <remarks>
    /// Все четыре условия обязательны. Люди — потому что допуск ссылается на подразделение по значению
    /// (без FK через границу схем, ТО-инф-06), и удаление оставило бы в допусках висячий номер, который
    /// решётка молча пропускала бы в никуда. Документы и поручения — потому что их владелец превратился
    /// бы в число без имени. Потомки — потому что они осиротели бы, потеряв ветку иерархии.
    /// </remarks>
    public bool CanDelete => Users == 0 && Documents == 0 && Assignments == 0 && Children == 0;
}

/// <summary>Итог операции над справочником подразделений.</summary>
public enum DivisionWriteResult
{
    /// <summary>Выполнено.</summary>
    Ok,

    /// <summary>Подразделение не найдено.</summary>
    NotFound,

    /// <summary>Подразделение используется (люди, документы, поручения) либо имеет дочерние.</summary>
    InUse,
}
