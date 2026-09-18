namespace ISC.AI.Profile.Investigation.Domain.Services;

/// <summary>Подразделение справочника с числом пользователей, у которых оно в допуске.</summary>
public sealed record DivisionNode(int Id, string Name, string? Code, int? ParentId, bool IsActive, int Users);

/// <summary>Исход записи в справочник.</summary>
public enum DivisionWriteResult
{
    /// <summary>Успех.</summary>
    Ok = 0,

    /// <summary>Подразделение не найдено.</summary>
    NotFound = 1,

    /// <summary>Есть дочерние или используется — удалить нельзя.</summary>
    InUse = 2,
}

/// <summary>Справочник подразделений профиля (ТФ-АДМ-01). Идентификаторы — словарь допусков ядра.</summary>
public interface IDivisionAdminStore
{
    /// <summary>Все подразделения (включая неактивные — для имён в допусках).</summary>
    Task<IReadOnlyList<DivisionNode>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Существует ли действующее подразделение.</summary>
    Task<bool> ExistsActiveAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>Создать; возвращает идентификатор.</summary>
    Task<int> CreateAsync(string name, string? code, int? parentId, CancellationToken cancellationToken = default);

    /// <summary>Переименовать.</summary>
    Task<DivisionWriteResult> RenameAsync(int id, string name, string? code, CancellationToken cancellationToken = default);

    /// <summary>Включить/выключить.</summary>
    Task<DivisionWriteResult> SetActiveAsync(int id, bool isActive, CancellationToken cancellationToken = default);
}
