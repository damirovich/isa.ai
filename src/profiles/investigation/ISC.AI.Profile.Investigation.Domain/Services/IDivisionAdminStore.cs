using ISC.AI.Profile.Investigation.Domain.Enums;

namespace ISC.AI.Profile.Investigation.Domain.Services;

/// <summary>Подразделение справочника с числом пользователей, у которых оно в допуске.</summary>
/// <remarks>
/// <paramref name="Direction"/> — собственная отметка отдела ОН/ОУ (ADR-0039); действующую с учётом вышестоящих даёт
/// <see cref="DivisionDirections"/>.
/// </remarks>
public sealed record DivisionNode(int Id, string Name, string? Code, int? ParentId, bool IsActive, int Users, CaseDirection? Direction = null);

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

    /// <summary>Создать с отметкой отдела ОН/ОУ (или без неё); возвращает идентификатор.</summary>
    Task<int> CreateAsync(string name, string? code, int? parentId, CaseDirection? direction, CancellationToken cancellationToken = default);

    /// <summary>
    /// Изменить наименование, код и отметку отдела ОН/ОУ. Смена отметки переносит в другой отдел все дела подразделения
    /// и вложенных без своей отметки — поэтому она записывается в журнал (ТБ-030).
    /// </summary>
    Task<DivisionWriteResult> RenameAsync(int id, string name, string? code, CaseDirection? direction, CancellationToken cancellationToken = default);

    /// <summary>Включить/выключить.</summary>
    Task<DivisionWriteResult> SetActiveAsync(int id, bool isActive, CancellationToken cancellationToken = default);
}
