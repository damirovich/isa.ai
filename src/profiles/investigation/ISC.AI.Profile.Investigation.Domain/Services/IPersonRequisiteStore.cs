using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Domain.Enums;

namespace ISC.AI.Profile.Investigation.Domain.Services;

/// <summary>Адрес фигуранта для карточки (ТФ-ПЕР-06).</summary>
/// <param name="Id">Идентификатор.</param>
/// <param name="PersonId">Фигурант.</param>
/// <param name="Kind">Вид адреса.</param>
/// <param name="Text">Адрес как введён.</param>
/// <param name="Notes">Примечание.</param>
public sealed record PersonAddressRow(int Id, int PersonId, AddressKind Kind, string Text, string? Notes);

/// <summary>Автотранспорт фигуранта для карточки (ТФ-ПЕР-06).</summary>
/// <param name="Id">Идентификатор.</param>
/// <param name="PersonId">Фигурант.</param>
/// <param name="PlateNumber">Госномер как введён.</param>
/// <param name="Make">Марка.</param>
/// <param name="Model">Модель.</param>
/// <param name="Color">Цвет.</param>
/// <param name="Notes">Примечание.</param>
public sealed record PersonVehicleRow(int Id, int PersonId, string? PlateNumber, string? Make, string? Model, string? Color, string? Notes);

/// <summary>Адреса и транспорт фигуранта.</summary>
/// <param name="Addresses">Адреса.</param>
/// <param name="Vehicles">Автотранспорт.</param>
public sealed record PersonRequisites(IReadOnlyList<PersonAddressRow> Addresses, IReadOnlyList<PersonVehicleRow> Vehicles);

/// <summary>Черновик адреса.</summary>
/// <param name="Kind">Вид адреса.</param>
/// <param name="Text">Адрес.</param>
/// <param name="Notes">Примечание.</param>
public sealed record PersonAddressDraft(AddressKind Kind, string Text, string? Notes);

/// <summary>Черновик автотранспорта: нужен госномер или хотя бы марка.</summary>
/// <param name="PlateNumber">Госномер.</param>
/// <param name="Make">Марка.</param>
/// <param name="Model">Модель.</param>
/// <param name="Color">Цвет.</param>
/// <param name="Notes">Примечание.</param>
public sealed record PersonVehicleDraft(string? PlateNumber, string? Make, string? Model, string? Color, string? Notes);

/// <summary>
/// Адреса и автотранспорт фигурантов (ТФ-ПЕР-06). Каждая операция — под решёткой фигуранта (floor ядра,
/// политика профиля и роль/владение через дело, ТБ-020/021): недоступный фигурант неотличим от
/// отсутствующего. Гриф и подразделение строки копируются с фигуранта (ТБ-070); нормализованное значение
/// (ТО-мат-11) хранилище вычисляет само.
/// </summary>
public interface IPersonRequisiteStore
{
    /// <summary>Адреса и транспорт фигуранта; <see langword="null"/> — фигурант недоступен.</summary>
    Task<PersonRequisites?> GetAsync(int personId, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>Добавить (<paramref name="addressId"/> = <see langword="null"/>) или изменить адрес фигуранта.</summary>
    Task<(PersonWriteResult Result, int AddressId)> SaveAddressAsync(
        int personId, int? addressId, PersonAddressDraft draft, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>Удалить адрес (ошибку ввода); запись об удалении — в журнале аудита.</summary>
    Task<PersonWriteResult> DeleteAddressAsync(int addressId, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>Добавить (<paramref name="vehicleId"/> = <see langword="null"/>) или изменить автотранспорт фигуранта.</summary>
    Task<(PersonWriteResult Result, int VehicleId)> SaveVehicleAsync(
        int personId, int? vehicleId, PersonVehicleDraft draft, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>Удалить автотранспорт (ошибку ввода); запись об удалении — в журнале аудита.</summary>
    Task<PersonWriteResult> DeleteVehicleAsync(int vehicleId, AccessContext access, CancellationToken cancellationToken = default);
}
