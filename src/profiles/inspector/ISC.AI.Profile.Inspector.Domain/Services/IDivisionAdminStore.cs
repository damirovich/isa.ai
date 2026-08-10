namespace ISC.AI.Profile.Inspector.Domain.Services;

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
