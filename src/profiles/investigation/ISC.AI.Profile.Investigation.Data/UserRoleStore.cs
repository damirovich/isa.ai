using ISC.AI.Persistence;
using ISC.AI.Profile.Investigation.Domain.Entities;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Profile.Investigation.Data;

/// <summary>
/// Ведение ролей пользователей (ТП-004) поверх <c>investigation.user_role_assignment</c>. Список
/// пользователей — из ядровой <c>core.app_user</c>; роль хранит профиль в своей схеме, связь по
/// <see cref="UserRoleAssignment.UserId"/> без FK через границу схем (ТО-инф-08) — два запроса в два
/// контекста, соединение по значению в памяти.
/// </summary>
public sealed class UserRoleStore(
    IDbContextFactory<CoreDbContext> coreContextFactory,
    IDbContextFactory<InvestigationDbContext> contextFactory) : IUserRoleStore
{
    /// <inheritdoc />
    public async Task<InvestigationRole?> GetRoleAsync(int userId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.UserRoleAssignments.AsNoTracking()
            .Where(r => r.UserId == userId)
            .Select(r => (InvestigationRole?)r.Role)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> AnyAdministratorAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.UserRoleAssignments.AsNoTracking()
            .AnyAsync(r => r.Role == InvestigationRole.Administrator, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<UserRoleRow>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var coreDb = await coreContextFactory.CreateDbContextAsync(cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var users = await coreDb.Users.AsNoTracking()
            .Where(u => u.IsActive)
            .Select(u => new { u.Id, Name = u.DisplayName ?? u.UserName })
            .ToListAsync(cancellationToken);

        var roles = await db.UserRoleAssignments.AsNoTracking()
            .ToDictionaryAsync(r => r.UserId, r => r.Role, cancellationToken);

        return users
            .OrderBy(u => u.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(u => new UserRoleRow(u.Id, u.Name, roles.TryGetValue(u.Id, out var role) ? role : null))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<int>> ListUserIdsByRoleAsync(InvestigationRole role, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.UserRoleAssignments.AsNoTracking()
            .Where(r => r.Role == role)
            .OrderBy(r => r.UserId)
            .Select(r => r.UserId)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task SetRoleAsync(int userId, InvestigationRole? role, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.UserRoleAssignments.FirstOrDefaultAsync(r => r.UserId == userId, cancellationToken);

        if (role is null)
        {
            if (existing is not null)
            {
                db.UserRoleAssignments.Remove(existing);
                await db.SaveChangesAsync(cancellationToken);
            }

            return;
        }

        if (existing is null)
        {
            db.UserRoleAssignments.Add(new UserRoleAssignment { UserId = userId, Role = role.Value });
        }
        else
        {
            existing.Role = role.Value;
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
