using ISC.AI.Profile.Inspector.Domain.Entities;
using ISC.AI.Profile.Inspector.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Profile.Inspector.Data;

/// <summary>Ведение справочника подразделений поверх <c>inspector.division</c> (§4.2, ТС-008).</summary>
public sealed class DivisionAdminStore(IDbContextFactory<InspectorDbContext> contextFactory) : IDivisionAdminStore
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<DivisionNode>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await db.Divisions.AsNoTracking()
            .OrderBy(d => d.Name)
            .Select(d => new DivisionNode(d.Id, d.Name, d.Code, d.ParentId))
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int> CreateAsync(
        string name, string? code, int? parentId, CancellationToken cancellationToken = default)
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
    public async Task<bool> RenameAsync(
        int id, string name, string? code, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var division = await db.Divisions.FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (division is null)
        {
            return false;
        }

        division.Name = name.Trim();
        division.Code = string.IsNullOrWhiteSpace(code) ? null : code.Trim();
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
