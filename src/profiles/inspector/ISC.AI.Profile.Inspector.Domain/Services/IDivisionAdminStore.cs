namespace ISC.AI.Profile.Inspector.Domain.Services;

/// <summary>
/// Узел справочника подразделений (иерархия ТУ→РО, §4.2): плоская строка с родителем.
/// </summary>
/// <param name="IsActive">Действующее ли подразделение (неактивное не предлагается при регистрации).</param>
/// <param name="Users">Сколько пользователей имеют это подразделение в допуске.</param>
/// <param name="Documents">Сколько документов принадлежит подразделению.</param>
/// <param name="Assignments">Сколько поручений выдано на подразделение.</param>
/// <param name="Children">Сколько дочерних подразделений.</param>
public sealed record DivisionNode(
    int Id,
    string Name,
    string? Code,
    int? ParentId,
    bool IsActive = true,
    int Users = 0,
    int Documents = 0,
    int Assignments = 0,
    int Children = 0)
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

/// <summary>
/// Порт ведения справочника подразделений профиля (<c>inspector.division</c>, §4.2). Иерархию ведёт
/// «ИнспекторAI»; модуль документооборота видит этот же словарь через <c>IDivisionDirectory</c>
/// (вопрос 3 Э4-35). <see cref="DivisionNode.Code"/> — ключ сопоставления с плоским справочником СКИД.
/// </summary>
public interface IDivisionAdminStore
{
    /// <summary>
    /// Все подразделения плоским списком со счётчиками использования (дерево строит UI по <c>ParentId</c>).
    /// </summary>
    /// <remarks>
    /// Счётчики считаются ВМЕСТЕ со списком: без них экран предлагал бы удаление там, где оно
    /// невозможно, а поштучный вопрос по каждой строке дал бы по обращению к трём базам на строку.
    /// </remarks>
    Task<IReadOnlyList<DivisionNode>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Создаёт подразделение (при <paramref name="parentId"/> — дочернее). Возвращает идентификатор.</summary>
    Task<int> CreateAsync(string name, string? code, int? parentId, CancellationToken cancellationToken = default);

    /// <summary>Меняет наименование и код. <see langword="false"/> — подразделение не найдено.</summary>
    Task<bool> RenameAsync(int id, string name, string? code, CancellationToken cancellationToken = default);

    /// <summary>Выводит подразделение из обращения или возвращает в него.</summary>
    Task<bool> SetActiveAsync(int id, bool isActive, CancellationToken cancellationToken = default);

    /// <summary>
    /// Удаляет подразделение. ИНВАРИАНТ: только если за ним ничего не числится и нет дочерних
    /// (<see cref="DivisionWriteResult.InUse"/>) — см. <see cref="DivisionNode.CanDelete"/>.
    /// </summary>
    Task<DivisionWriteResult> DeleteAsync(int id, CancellationToken cancellationToken = default);
}
