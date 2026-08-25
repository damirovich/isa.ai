using ISC.AI.Modules.DocFlow.Domain.Services;
using ISC.AI.Persistence;
using ISC.AI.Profile.Inspector.Domain.Entities;
using ISC.AI.Profile.Inspector.Domain.Enums;
using ISC.AI.Profile.Inspector.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Profile.Inspector.Data;

/// <summary>Ведение справочника подразделений поверх <c>inspector.division</c> (§4.2, ТС-008).</summary>
public sealed class DivisionAdminStore(
    IDbContextFactory<InspectorDbContext> contextFactory,
    IDbContextFactory<CoreDbContext> coreContextFactory,
    IDivisionUsage divisionUsage) : IDivisionAdminStore
{
    /// <inheritdoc />
    public async Task<bool> ExistsActiveAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Divisions.AsNoTracking().AnyAsync(d => d.Id == id && d.IsActive, cancellationToken);
    }

    public async Task<IReadOnlyList<DivisionNode>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var divisions = await db.Divisions.AsNoTracking()
            .OrderBy(d => d.Name)
            .Select(d => new
            {
                d.Id,
                d.Name,
                d.Code,
                d.ParentId,
                d.IsActive,
                d.Kind,
                Children = db.Divisions.Count(child => child.ParentId == d.Id),
            })
            .ToListAsync(cancellationToken);

        // Счётчики собираются из ТРЁХ схем — иначе никак: справочник в inspector, допуски в core,
        // поручения в docflow. Каждый источник спрашивается ОДИН раз на весь список, не построчно.
        var usage = await divisionUsage.CountAsync(cancellationToken);
        var users = await CountUsersByDivisionAsync(cancellationToken);

        return divisions
            .Select(d =>
            {
                var used = usage.TryGetValue(d.Id, out var value) ? value : DivisionUsage.None;
                return new DivisionNode(
                    d.Id,
                    d.Name,
                    d.Code,
                    d.ParentId,
                    d.IsActive,
                    users.TryGetValue(d.Id, out var userCount) ? userCount : 0,
                    used.Documents,
                    used.Assignments,
                    d.Children,
                    d.Kind);
            })
            .ToList();
    }

    /// <summary>
    /// Сколько пользователей имеют подразделение в допуске (<c>core.clearance.division_scope</c>).
    /// </summary>
    /// <remarks>
    /// Разворачивается В ПАМЯТИ: <c>division_scope</c> — массив, и группировка по его элементам
    /// потребовала бы разворачивания массива средствами Postgres, которое EF не переводит. Строка
    /// допуска одна на пользователя, их столько же, сколько сотрудников, — это дёшево.
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

    /// <inheritdoc />
    public async Task<int> CreateAsync(
        string name, string? code, int? parentId,
        DivisionKind kind = DivisionKind.Territorial, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var division = new Division
        {
            Name = name.Trim(),
            Code = string.IsNullOrWhiteSpace(code) ? null : code.Trim(),
            ParentId = parentId,
            Kind = kind,
        };
        db.Divisions.Add(division);
        await db.SaveChangesAsync(cancellationToken);
        return division.Id;
    }

    /// <inheritdoc />
    public async Task<bool> RenameAsync(
        int id, string name, string? code,
        DivisionKind kind = DivisionKind.Territorial, CancellationToken cancellationToken = default)
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
        division.Kind = kind;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> SetActiveAsync(
        int id, bool isActive, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var division = await db.Divisions.FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (division is null)
        {
            return false;
        }

        division.IsActive = isActive;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <inheritdoc />
    public async Task<DivisionWriteResult> DeleteAsync(
        int id, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var division = await db.Divisions.FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (division is null)
        {
            return DivisionWriteResult.NotFound;
        }

        // Все проверки ПОВТОРЯЮТСЯ здесь, хотя признак CanDelete уже посчитан для списка: между
        // отрисовкой экрана и нажатием кнопки успевают появиться и документ, и поручение, и допуск.
        if (await db.Divisions.AnyAsync(child => child.ParentId == id, cancellationToken))
        {
            return DivisionWriteResult.InUse;
        }

        var users = await CountUsersByDivisionAsync(cancellationToken);
        if (users.ContainsKey(id))
        {
            return DivisionWriteResult.InUse;
        }

        var usage = await divisionUsage.CountAsync(cancellationToken);
        if (usage.TryGetValue(id, out var used) && !used.IsEmpty)
        {
            return DivisionWriteResult.InUse;
        }

        db.Divisions.Remove(division);
        await db.SaveChangesAsync(cancellationToken);
        return DivisionWriteResult.Ok;
    }
}
