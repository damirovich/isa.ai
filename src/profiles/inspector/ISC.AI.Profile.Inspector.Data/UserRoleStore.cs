using ISC.AI.Persistence;
using ISC.AI.Profile.Inspector.Domain.Entities;
using ISC.AI.Profile.Inspector.Domain.Enums;
using ISC.AI.Profile.Inspector.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Profile.Inspector.Data;

/// <summary>
/// Ведение ролей пользователей (§2.1 ТЗ СКИД) поверх <c>inspector.user_role_assignment</c>. Список
/// пользователей — из ядровой <c>core.app_user</c> (та же таблица, что и у докфлоу-модуля, ТС-008);
/// роль хранит профиль в своей схеме, связь по <see cref="UserRoleAssignment.UserId"/> без FK через
/// границу схем (ТО-инф-06) — два запроса в два контекста, соединение по значению в памяти.
/// </summary>
public sealed class UserRoleStore(
    IDbContextFactory<CoreDbContext> coreContextFactory,
    IDbContextFactory<InspectorDbContext> inspectorContextFactory) : IUserRoleStore
{
    /// <inheritdoc />
    public async Task<UserRole?> GetRoleAsync(int userId, CancellationToken cancellationToken = default)
    {
        await using var db = await inspectorContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.UserRoleAssignments.AsNoTracking()
            .Where(r => r.UserId == userId)
            .Select(r => (UserRole?)r.Role)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> AnyAdministratorAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await inspectorContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.UserRoleAssignments.AsNoTracking()
            .AnyAsync(r => r.Role == UserRole.Administrator, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<UserRoleRow>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var coreDb = await coreContextFactory.CreateDbContextAsync(cancellationToken);
        await using var inspectorDb = await inspectorContextFactory.CreateDbContextAsync(cancellationToken);

        var users = await coreDb.Users.AsNoTracking()
            .Where(u => u.IsActive)
            .Select(u => new { u.Id, Name = u.DisplayName ?? u.UserName })
            .ToListAsync(cancellationToken);

        var roles = await inspectorDb.UserRoleAssignments.AsNoTracking()
            .ToDictionaryAsync(r => r.UserId, r => r.Role, cancellationToken);

        return users
            .OrderBy(u => u.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(u => new UserRoleRow(u.Id, u.Name, roles.TryGetValue(u.Id, out var role) ? role : null))
            .ToList();
    }

    /// <inheritdoc />
    public async Task SetRoleAsync(int userId, UserRole? role, CancellationToken cancellationToken = default)
    {
        await using var db = await inspectorContextFactory.CreateDbContextAsync(cancellationToken);
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

    /// <inheritdoc />
    /// <remarks>
    /// Не кэшируется НАМЕРЕННО (ТБ-016): снятая галочка действует со следующего же запроса. Запрос — по уникальному
    /// индексу (role, permission) маленькой таблицы.
    /// </remarks>
    public async Task<bool?> GetPermissionOverrideAsync(UserRole role, string permission, CancellationToken cancellationToken = default)
    {
        await using var db = await inspectorContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.RolePermissions.AsNoTracking()
            .Where(p => p.Role == role && p.Permission == permission)
            .Select(p => (bool?)p.IsGranted)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RolePermissionOverride>> ListPermissionOverridesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await inspectorContextFactory.CreateDbContextAsync(cancellationToken);
        return await db.RolePermissions.AsNoTracking()
            .OrderBy(p => p.Permission).ThenBy(p => p.Role)
            .Select(p => new RolePermissionOverride(p.Role, p.Permission, p.IsGranted, p.UpdatedByUserId, p.UpdatedAt ?? p.CreatedAt))
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task ApplyPermissionChangesAsync(
        IReadOnlyCollection<RolePermissionChange> changes, int? changedByUserId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.Count == 0)
        {
            return;
        }

        await using var db = await inspectorContextFactory.CreateDbContextAsync(cancellationToken);

        // Вся пачка — один SaveChanges, то есть одна транзакция: матрица не остаётся «наполовину применённой».
        var existing = await db.RolePermissions.ToListAsync(cancellationToken);
        foreach (var change in changes)
        {
            var row = existing.FirstOrDefault(p => p.Role == change.Role
                && string.Equals(p.Permission, change.Permission, StringComparison.Ordinal));

            if (change.IsGranted is not { } granted)
            {
                if (row is not null)
                {
                    db.RolePermissions.Remove(row);
                    existing.Remove(row);
                }

                continue;
            }

            if (row is null)
            {
                row = new RolePermission { Role = change.Role, Permission = change.Permission };
                db.RolePermissions.Add(row);
                existing.Add(row);
            }

            row.IsGranted = granted;
            row.UpdatedByUserId = changedByUserId;
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
