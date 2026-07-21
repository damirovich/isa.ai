using System.Globalization;
using ISC.AI.Abstractions.Security;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Persistence.Security;

/// <summary>
/// Чтение действующего допуска субъекта из <c>core.app_user</c>/<c>core.clearance</c> и свёртка его в
/// <see cref="AccessContext"/> (Э3-08, ТБ-011/012).
/// </summary>
/// <remarks>
/// ИНВАРИАНТ БЕЗОПАСНОСТИ (ТД-004, ТБ-016): допуск читается из БД НА КАЖДУЮ операцию и нигде не
/// кэшируется — отзыв допуска (мягкое удаление записи <c>clearance</c>) или деактивация пользователя
/// действует для retrieval немедленно, даже при живой сессии. FAIL-CLOSED (ТБ-021): нет пользователя /
/// пользователь неактивен / допуска нет — возвращается <c>null</c>, а не «пустой» доступ.
/// </remarks>
public sealed class ClearanceAccessReader(IDbContextFactory<CoreDbContext> contextFactory)
{
    /// <summary>
    /// Возвращает контекст доступа субъекта по локальному идентификатору пользователя либо <c>null</c>,
    /// если действующего допуска нет (в т.ч. пользователь неактивен или допуск отозван).
    /// </summary>
    public async Task<AccessContext?> ReadAsync(int userId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Глобальный фильтр мягкого удаления скрывает отозванные допуски и удалённых пользователей —
        // отзыв виден этому запросу сразу же (ТБ-016).
        var user = await db.Users.AsNoTracking()
            .Include(u => u.Clearance)
            .FirstOrDefaultAsync(u => u.Id == userId && u.IsActive, cancellationToken);

        if (user?.Clearance is null)
        {
            return null;
        }

        return new AccessContext(
            user.Id.ToString(CultureInfo.InvariantCulture),
            user.Clearance.MaxClassification,
            user.Clearance.DivisionScope);
    }
}
