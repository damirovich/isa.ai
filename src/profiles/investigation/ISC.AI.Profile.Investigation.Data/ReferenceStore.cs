using ISC.AI.Profile.Investigation.Domain.Entities;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ISC.AI.Profile.Investigation.Data;

/// <summary>
/// Справочники профиля поверх <c>investigation.reference_item</c> (ТФ-АДМ-07, ТС-008). Наименование
/// уникально в пределах вида без учёта регистра: сверка — здесь, уникальный индекс (вид, наименование) —
/// страховка от гонки двух одновременных записей.
/// </summary>
public sealed class ReferenceStore(IDbContextFactory<InvestigationDbContext> contextFactory) : IReferenceStore
{
    private const string UniqueViolation = "23505";

    /// <inheritdoc />
    public async Task<IReadOnlyList<ReferenceItemRow>> ListAsync(ReferenceKind? kind, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var query = db.ReferenceItems.AsNoTracking();
        if (kind is { } k)
        {
            query = query.Where(i => i.Kind == k);
        }

        return await query
            .OrderBy(i => i.Kind)
            .ThenBy(i => i.SortOrder)
            .ThenBy(i => i.Name)
            .Select(i => new ReferenceItemRow(i.Id, i.Kind, i.Name, i.Code, i.SortOrder, i.IsActive))
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<(ReferenceWriteResult Result, int Id)> CreateAsync(
        ReferenceKind kind, string name, string? code, int sortOrder, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var clean = name.Trim();
        if (await NameTakenAsync(db, kind, clean, exceptId: null, cancellationToken))
        {
            return (ReferenceWriteResult.Duplicate, 0);
        }

        var item = new ReferenceItem
        {
            Kind = kind,
            Name = clean,
            Code = Clean(code),
            SortOrder = sortOrder,
        };
        db.ReferenceItems.Add(item);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: UniqueViolation })
        {
            return (ReferenceWriteResult.Duplicate, 0);
        }

        return (ReferenceWriteResult.Ok, item.Id);
    }

    /// <inheritdoc />
    public async Task<ReferenceWriteResult> UpdateAsync(
        int id, string name, string? code, int sortOrder, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var item = await db.ReferenceItems.FirstOrDefaultAsync(i => i.Id == id, cancellationToken);
        if (item is null)
        {
            return ReferenceWriteResult.NotFound;
        }

        var clean = name.Trim();
        if (await NameTakenAsync(db, item.Kind, clean, exceptId: id, cancellationToken))
        {
            return ReferenceWriteResult.Duplicate;
        }

        // Переименование видно во всех делах, где стоит запись: справочник — словарь, а не снимок.
        // Это и нужно для исправления опечатки; смена смысла записи — это новая запись и выключение старой.
        item.Name = clean;
        item.Code = Clean(code);
        item.SortOrder = sortOrder;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: UniqueViolation })
        {
            return ReferenceWriteResult.Duplicate;
        }

        return ReferenceWriteResult.Ok;
    }

    /// <inheritdoc />
    public async Task<ReferenceWriteResult> SetActiveAsync(int id, bool isActive, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var item = await db.ReferenceItems.FirstOrDefaultAsync(i => i.Id == id, cancellationToken);
        if (item is null)
        {
            return ReferenceWriteResult.NotFound;
        }

        item.IsActive = isActive;
        await db.SaveChangesAsync(cancellationToken);
        return ReferenceWriteResult.Ok;
    }

    /// <summary>
    /// Занято ли наименование в справочнике без учёта регистра: ILIKE без подстановочных символов —
    /// «%», «_» и «\» в наименовании экранируются и сравниваются буквально.
    /// </summary>
    private static Task<bool> NameTakenAsync(
        InvestigationDbContext db, ReferenceKind kind, string name, int? exceptId, CancellationToken cancellationToken)
    {
        var literal = name
            .Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal);
        return db.ReferenceItems.AsNoTracking()
            .AnyAsync(i => i.Kind == kind && EF.Functions.ILike(i.Name, literal, @"\") && i.Id != exceptId, cancellationToken);
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
