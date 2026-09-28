using ISC.AI.Profile.Investigation.Domain.Enums;

namespace ISC.AI.Profile.Investigation.Domain.Services;

/// <summary>Запись справочника профиля для списков и форм (ТФ-АДМ-07).</summary>
/// <param name="Id">Идентификатор.</param>
/// <param name="Kind">Вид справочника.</param>
/// <param name="Name">Наименование.</param>
/// <param name="Code">Код.</param>
/// <param name="SortOrder">Порядок в списках.</param>
/// <param name="IsActive">Действующая (предлагается при вводе).</param>
public sealed record ReferenceItemRow(int Id, ReferenceKind Kind, string Name, string? Code, int SortOrder, bool IsActive);

/// <summary>Исход записи в справочник профиля.</summary>
public enum ReferenceWriteResult
{
    /// <summary>Успех.</summary>
    Ok = 0,

    /// <summary>Запись не найдена.</summary>
    NotFound = 1,

    /// <summary>Запись с таким наименованием в этом справочнике уже есть (без учёта регистра).</summary>
    Duplicate = 2,
}

/// <summary>
/// Справочники профиля (ТФ-АДМ-07): подразделения-инициаторы, звания, должности, типы связей, категории
/// материалов. Удаления нет намеренно — на записи ссылаются дела; вместо удаления запись выключается.
/// </summary>
public interface IReferenceStore
{
    /// <summary>Записи справочника (или всех справочников при <paramref name="kind"/> = <see langword="null"/>), включая выключенные.</summary>
    Task<IReadOnlyList<ReferenceItemRow>> ListAsync(ReferenceKind? kind, CancellationToken cancellationToken = default);

    /// <summary>Создать запись.</summary>
    Task<(ReferenceWriteResult Result, int Id)> CreateAsync(ReferenceKind kind, string name, string? code, int sortOrder, CancellationToken cancellationToken = default);

    /// <summary>Изменить наименование, код и порядок (вид записи не меняется).</summary>
    Task<ReferenceWriteResult> UpdateAsync(int id, string name, string? code, int sortOrder, CancellationToken cancellationToken = default);

    /// <summary>Включить/выключить запись.</summary>
    Task<ReferenceWriteResult> SetActiveAsync(int id, bool isActive, CancellationToken cancellationToken = default);
}
