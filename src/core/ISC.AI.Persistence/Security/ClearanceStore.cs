using ISC.AI.Abstractions.Security;
using ISC.AI.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Persistence.Security;

/// <summary>
/// Ведение допусков поверх <c>core.clearance</c> (реализация <see cref="IClearanceStore"/>).
/// Контекст — на операцию, через фабрику (ТС-008).
/// </summary>
public sealed class ClearanceStore(IDbContextFactory<CoreDbContext> contextFactory) : IClearanceStore
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<ClearanceRow>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Глобальный фильтр мягкого удаления уже скрывает отозванные допуски и удалённых пользователей.
        var rows = await db.Users.AsNoTracking()
            .Include(u => u.Clearance)
            .Select(u => new
            {
                u.Id,
                Name = u.DisplayName ?? u.UserName,
                u.IsActive,
                MaxClassification = u.Clearance != null ? (short?)u.Clearance.MaxClassification : null,
                DivisionScope = u.Clearance != null ? u.Clearance.DivisionScope : null,
            })
            .ToListAsync(cancellationToken);

        return rows
            .OrderBy(u => u.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(u => new ClearanceRow(
                u.Id,
                u.Name,
                u.IsActive,
                u.MaxClassification,
                u.DivisionScope is { } scope ? [.. scope.Distinct().Order()] : []))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<bool> SetAsync(
        int userId, short maxClassification, IReadOnlyList<int> divisionScope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(divisionScope);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Допуск выдаётся только существующему АКТИВНОМУ пользователю: на отключённую учётку он всё
        // равно не подействует (ClearanceAccessReader читает с условием IsActive) — молчаливая запись
        // «в никуда» создавала бы ложное впечатление выданного доступа.
        var user = await db.Users.AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId && u.IsActive, cancellationToken);
        if (user is null)
        {
            return false;
        }

        var scope = divisionScope.Where(id => id > 0).Distinct().Order().ToList();

        var existing = await db.Clearances.FirstOrDefaultAsync(c => c.UserId == userId, cancellationToken);
        if (existing is null)
        {
            db.Clearances.Add(new ClearanceEntity
            {
                UserId = userId,
                MaxClassification = maxClassification,
                DivisionScope = scope,
            });
        }
        else
        {
            existing.MaxClassification = maxClassification;
            existing.DivisionScope = scope;
        }

        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> RevokeAsync(int userId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var existing = await db.Clearances.FirstOrDefaultAsync(c => c.UserId == userId, cancellationToken);
        if (existing is null)
        {
            return false;
        }

        // Удаление МЯГКОЕ (перехватывается контекстом): история выдачи допусков не стирается, а
        // частичный уникальный индекс по живым записям позволяет выдать допуск заново (ТБ-016).
        db.Clearances.Remove(existing);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
