using ISC.AI.Modules.DocFlow.Domain.Services;
using ISC.AI.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Modules.DocFlow.Data;

/// <summary>
/// Справочник пользователей поверх реестра ядра <c>core.app_user</c> (вопрос 4 Э4-35: реестр — в ISC.AI).
/// Слой данных модуля вправе ссылаться на <c>Persistence</c> (правило зависимостей, как у профиля).
/// </summary>
public sealed class UserDirectory(IDbContextFactory<CoreDbContext> contextFactory) : IUserDirectory
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<UserItem>> ListActiveAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await db.Users.AsNoTracking()
            .Where(u => u.IsActive)
            .OrderBy(u => u.DisplayName ?? u.UserName)
            .Select(u => new UserItem(u.Id, u.DisplayName ?? u.UserName))
            .ToListAsync(cancellationToken);
    }
}
