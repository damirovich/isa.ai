using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Domain.Entities;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Profile.Investigation.Data;

/// <summary>
/// Адреса и автотранспорт фигурантов поверх <c>investigation.person_address</c> / <c>person_vehicle</c>
/// (ТФ-ПЕР-06). Каждая операция начинается с видимости фигуранта (<see cref="PersonAccess"/>): недоступный
/// фигурант, как и чужой адрес, неотличим от отсутствующего (ТБ-021). Строки читаются ещё и под собственным
/// floor'ом (у них свои режимные поля, ТБ-070). Нормализованный адрес и госномер (ТО-мат-11) вычисляются здесь,
/// при каждой записи, — вызывающий их не передаёт.
/// </summary>
public sealed class PersonRequisiteStore(
    IDbContextFactory<InvestigationDbContext> contextFactory,
    IAccessPolicy policy,
    IUserRoleStore roles) : IPersonRequisiteStore
{
    /// <inheritdoc />
    public async Task<PersonRequisites?> GetAsync(int personId, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);

        var role = await ResolveRoleAsync(access, cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var visible = PersonAccess.Accessible(db, access, policy, role).Where(p => p.Id == personId);
        if (!await visible.AnyAsync(cancellationToken))
        {
            return null;
        }

        var addresses = await db.PersonAddresses.AsNoTracking()
            .Where(BaselineAccess.Filter<PersonAddress>(access))
            .Where(policy.BuildFilter<PersonAddress>(access))
            .Where(a => a.PersonId == personId)
            .OrderBy(a => a.Kind)
            .ThenBy(a => a.Id)
            .Select(a => new PersonAddressRow(a.Id, a.PersonId, a.Kind, a.Text, a.Notes))
            .ToListAsync(cancellationToken);

        var vehicles = await db.PersonVehicles.AsNoTracking()
            .Where(BaselineAccess.Filter<PersonVehicle>(access))
            .Where(policy.BuildFilter<PersonVehicle>(access))
            .Where(v => v.PersonId == personId)
            .OrderBy(v => v.Id)
            .Select(v => new PersonVehicleRow(v.Id, v.PersonId, v.PlateNumber, v.Make, v.Model, v.Color, v.Notes))
            .ToListAsync(cancellationToken);

        return new PersonRequisites(addresses, vehicles);
    }

    /// <inheritdoc />
    public async Task<(PersonWriteResult Result, int AddressId)> SaveAddressAsync(
        int personId, int? addressId, PersonAddressDraft draft, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(access);
        ArgumentException.ThrowIfNullOrWhiteSpace(draft.Text);

        var role = await ResolveRoleAsync(access, cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var person = await PersonAccess.Accessible(db, access, policy, role)
            .Where(p => p.Id == personId)
            .Select(p => new { p.Id, p.Classification, p.DivisionId })
            .FirstOrDefaultAsync(cancellationToken);
        if (person is null)
        {
            return (PersonWriteResult.NotFound, 0);
        }

        PersonAddress address;
        if (addressId is { } id)
        {
            // Правка — только адреса ЭТОГО фигуранта: чужой идентификатор неотличим от отсутствующего.
            var existing = await db.PersonAddresses.FirstOrDefaultAsync(a => a.Id == id && a.PersonId == person.Id, cancellationToken);
            if (existing is null)
            {
                return (PersonWriteResult.NotFound, 0);
            }

            address = existing;
        }
        else
        {
            address = new PersonAddress
            {
                PersonId = person.Id,
                Text = string.Empty,
                TextNormalized = string.Empty,
                // ТБ-070: режимные поля строки — с фигуранта, не из черновика.
                Classification = person.Classification,
                DivisionId = person.DivisionId,
            };
            db.PersonAddresses.Add(address);
        }

        address.Kind = draft.Kind;
        address.Text = draft.Text.Trim();
        address.TextNormalized = RequisiteNormalizer.Address(address.Text) ?? string.Empty;
        address.Notes = Clean(draft.Notes);

        await db.SaveChangesAsync(cancellationToken);
        return (PersonWriteResult.Ok, address.Id);
    }

    /// <inheritdoc />
    public async Task<PersonWriteResult> DeleteAddressAsync(int addressId, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);

        var role = await ResolveRoleAsync(access, cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var visible = PersonAccess.Accessible(db, access, policy, role);
        var address = await db.PersonAddresses
            .FirstOrDefaultAsync(a => a.Id == addressId && visible.Any(p => p.Id == a.PersonId), cancellationToken);
        if (address is null)
        {
            return PersonWriteResult.NotFound;
        }

        db.PersonAddresses.Remove(address);
        await db.SaveChangesAsync(cancellationToken);
        return PersonWriteResult.Ok;
    }

    /// <inheritdoc />
    public async Task<(PersonWriteResult Result, int VehicleId)> SaveVehicleAsync(
        int personId, int? vehicleId, PersonVehicleDraft draft, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(access);
        if (string.IsNullOrWhiteSpace(draft.PlateNumber) && string.IsNullOrWhiteSpace(draft.Make))
        {
            throw new ArgumentException("Нужен госномер или хотя бы марка.", nameof(draft));
        }

        var role = await ResolveRoleAsync(access, cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var person = await PersonAccess.Accessible(db, access, policy, role)
            .Where(p => p.Id == personId)
            .Select(p => new { p.Id, p.Classification, p.DivisionId })
            .FirstOrDefaultAsync(cancellationToken);
        if (person is null)
        {
            return (PersonWriteResult.NotFound, 0);
        }

        PersonVehicle vehicle;
        if (vehicleId is { } id)
        {
            var existing = await db.PersonVehicles.FirstOrDefaultAsync(v => v.Id == id && v.PersonId == person.Id, cancellationToken);
            if (existing is null)
            {
                return (PersonWriteResult.NotFound, 0);
            }

            vehicle = existing;
        }
        else
        {
            vehicle = new PersonVehicle
            {
                PersonId = person.Id,
                Classification = person.Classification,
                DivisionId = person.DivisionId,
            };
            db.PersonVehicles.Add(vehicle);
        }

        vehicle.PlateNumber = Clean(draft.PlateNumber);
        vehicle.PlateNormalized = RequisiteNormalizer.Plate(vehicle.PlateNumber);
        vehicle.Make = Clean(draft.Make);
        vehicle.Model = Clean(draft.Model);
        vehicle.Color = Clean(draft.Color);
        vehicle.Notes = Clean(draft.Notes);

        await db.SaveChangesAsync(cancellationToken);
        return (PersonWriteResult.Ok, vehicle.Id);
    }

    /// <inheritdoc />
    public async Task<PersonWriteResult> DeleteVehicleAsync(int vehicleId, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);

        var role = await ResolveRoleAsync(access, cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var visible = PersonAccess.Accessible(db, access, policy, role);
        var vehicle = await db.PersonVehicles
            .FirstOrDefaultAsync(v => v.Id == vehicleId && visible.Any(p => p.Id == v.PersonId), cancellationToken);
        if (vehicle is null)
        {
            return PersonWriteResult.NotFound;
        }

        db.PersonVehicles.Remove(vehicle);
        await db.SaveChangesAsync(cancellationToken);
        return PersonWriteResult.Ok;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // Роль для правила видимости дел: без права «Дашборд и реестр дел» (матрица доступа, ADR-0032) — null, и
    // CaseAccessRule вернёт пусто (ТБ-012/021).
    private Task<InvestigationRole?> ResolveRoleAsync(AccessContext access, CancellationToken cancellationToken) =>
        PermissionRule.ResolveCaseViewerAsync(roles, access.NumericSubjectId, cancellationToken);
}
