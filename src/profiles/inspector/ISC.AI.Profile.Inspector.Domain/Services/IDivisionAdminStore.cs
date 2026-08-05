namespace ISC.AI.Profile.Inspector.Domain.Services;

/// <summary>Узел справочника подразделений (иерархия ТУ→РО, §4.2): плоская строка с родителем.</summary>
public sealed record DivisionNode(int Id, string Name, string? Code, int? ParentId);

/// <summary>
/// Порт ведения справочника подразделений профиля (<c>inspector.division</c>, §4.2). Иерархию ведёт
/// «ИнспекторAI»; модуль документооборота видит этот же словарь через <c>IDivisionDirectory</c>
/// (вопрос 3 Э4-35). <see cref="DivisionNode.Code"/> — ключ сопоставления с плоским справочником СКИД.
/// </summary>
public interface IDivisionAdminStore
{
    /// <summary>Все подразделения плоским списком (дерево строит UI по <c>ParentId</c>).</summary>
    Task<IReadOnlyList<DivisionNode>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Создаёт подразделение (при <paramref name="parentId"/> — дочернее). Возвращает идентификатор.</summary>
    Task<int> CreateAsync(string name, string? code, int? parentId, CancellationToken cancellationToken = default);

    /// <summary>Меняет наименование и код. <see langword="false"/> — подразделение не найдено.</summary>
    Task<bool> RenameAsync(int id, string name, string? code, CancellationToken cancellationToken = default);
}
