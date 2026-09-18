using ISC.AI.Persistence;
using ISC.AI.Profile.Investigation.Domain.Entities;
using ISC.AI.Profile.Investigation.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Profile.Investigation.Data;

/// <summary>Ведение справочника подразделений поверх <c>investigation.division</c> (ТФ-АДМ-01, ТС-008).</summary>
public sealed class DivisionAdminStore(
    IDbContextFactory<InvestigationDbContext> contextFactory,
    IDbContextFactory<CoreDbContext> coreContextFactory) : IDivisionAdminStore
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<DivisionNode>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var divisions = await db.Divisions.AsNoTracking()
            .OrderBy(d => d.Name)
            .Select(d => new { d.Id, d.Name, d.Code, d.ParentId, d.IsActive })
            .ToListAsync(cancellationToken);

        var users = await CountUsersByDivisionAsync(cancellationToken);

        return divisions
            .Select(d => new DivisionNode(
                d.Id, d.Name, d.Code, d.ParentId, d.IsActive,
                users.TryGetValue(d.Id, out var count) ? count : 0))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<bool> ExistsActiveAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Divisions.AsNoTracking().AnyAsync(d => d.Id == id && d.IsActive, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int> CreateAsync(string name, string? code, int? parentId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var division = new Division
        {
            Name = name.Trim(),
            Code = string.IsNullOrWhiteSpace(code) ? null : code.Trim(),
            ParentId = parentId,
        };
        db.Divisions.Add(division);
        await db.SaveChangesAsync(cancellationToken);
        return division.Id;
    }

    /// <inheritdoc />
    public async Task<DivisionWriteResult> RenameAsync(int id, string name, string? code, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var division = await db.Divisions.FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (division is null)
        {
            return DivisionWriteResult.NotFound;
        }

        division.Name = name.Trim();
        division.Code = string.IsNullOrWhiteSpace(code) ? null : code.Trim();
        await db.SaveChangesAsync(cancellationToken);
        return DivisionWriteResult.Ok;
    }

    /// <inheritdoc />
    public async Task<DivisionWriteResult> SetActiveAsync(int id, bool isActive, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var division = await db.Divisions.FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (division is null)
        {
            return DivisionWriteResult.NotFound;
        }

        division.IsActive = isActive;
        await db.SaveChangesAsync(cancellationToken);
        return DivisionWriteResult.Ok;
    }

    /// <summary>Сколько пользователей имеют подразделение в допуске (<c>core.clearance.division_scope</c>).</summary>
    /// <remarks>
    /// Разворачивается В ПАМЯТИ: <c>division_scope</c> — массив, и группировка по его элементам
    /// потребовала бы unnest на стороне Postgres, который EF не переводит. Строка допуска одна на
    /// пользователя — их столько же, сколько сотрудников, это дёшево.
    /// </remarks>
    private async Task<Dictionary<int, int>> CountUsersByDivisionAsync(CancellationToken cancellationToken)
    {
        await using var core = await coreContextFactory.CreateDbContextAsync(cancellationToken);

        var scopes = await core.Clearances.AsNoTracking()
            .Select(c => c.DivisionScope)
            .ToListAsync(cancellationToken);

        var counts = new Dictionary<int, int>();
        foreach (var divisionId in scopes.SelectMany(scope => scope))
        {
            counts[divisionId] = counts.TryGetValue(divisionId, out var value) ? value + 1 : 1;
        }

        return counts;
    }
}
