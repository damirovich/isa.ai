using ISC.AI.Profile.Investigation.Domain.Entities;
using ISC.AI.Profile.Investigation.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Profile.Investigation.Data;

/// <summary>
/// История единого поиска сотрудника поверх <c>investigation.search_history_entry</c> (ADR-0049).
/// </summary>
/// <remarks>
/// ИНВАРИАНТ (ТБ-020/021): каждая выборка и каждое изменение ограничены владельцем (<c>user_id</c> = вошедший сотрудник,
/// его передаёт вызывающий из сессии), чтение — ещё и грифом строки не выше текущего допуска. Чужая запись неотличима от
/// отсутствующей. Время — UTC (<see cref="TimeProvider"/> — ради проверяемого порядка «новые первыми»).
/// </remarks>
public sealed class SearchHistoryStore(IDbContextFactory<InvestigationDbContext> contextFactory, TimeProvider time) : ISearchHistoryStore
{
    /// <inheritdoc />
    public async Task RecordAsync(int userId, SearchHistoryDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (!draft.Terms.IsSearchable)
        {
            return;
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var now = time.GetUtcNow().UtcDateTime;
        var key = draft.Terms.Text;
        var direction = draft.Direction;

        // Тот же запрос — та же строка: наверх с новым временем; текст — как ввели в этот раз.
        var existing = await db.SearchHistory
            .Where(e => e.UserId == userId && e.TextKey == key && e.Kinds == draft.Kinds && e.Direction == direction)
            .OrderByDescending(e => e.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (existing is null)
        {
            db.SearchHistory.Add(new SearchHistoryEntry
            {
                UserId = userId,
                Text = draft.Terms.Raw,
                TextKey = key,
                Kinds = draft.Kinds,
                Direction = direction,
                ResultCount = draft.ResultCount,
                Classification = draft.Classification,
                SearchedAt = now,
            });
        }
        else
        {
            existing.Text = draft.Terms.Raw;
            existing.ResultCount = draft.ResultCount;
            existing.SearchedAt = now;
            // Гриф — не ниже прежнего: строка могла быть записана при более высоком допуске.
            existing.Classification = Math.Max(existing.Classification, draft.Classification);
        }

        await db.SaveChangesAsync(cancellationToken);

        // Вытеснение: незакреплённые сверх предела — самые старые.
        var keep = db.SearchHistory
            .Where(e => e.UserId == userId && !e.IsPinned)
            .OrderByDescending(e => e.SearchedAt).ThenByDescending(e => e.Id)
            .Take(ISearchHistoryStore.MaxRecent)
            .Select(e => e.Id);
        await db.SearchHistory
            .Where(e => e.UserId == userId && !e.IsPinned && !keep.Contains(e.Id))
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SearchHistoryRow>> ListAsync(int userId, short maxClassification, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.SearchHistory.AsNoTracking()
            .Where(e => e.UserId == userId && e.Classification <= maxClassification)
            .OrderByDescending(e => e.IsPinned).ThenByDescending(e => e.SearchedAt).ThenByDescending(e => e.Id)
            .Select(e => new SearchHistoryRow(e.Id, e.Text, e.Kinds, e.Direction, e.ResultCount, e.IsPinned, e.SearchedAt))
            .ToListAsync(cancellationToken);
        return rows.Select(r => r with { SearchedAt = DateTime.SpecifyKind(r.SearchedAt, DateTimeKind.Utc) }).ToList();
    }

    /// <inheritdoc />
    public async Task<SearchHistoryWriteResult> SetPinnedAsync(int userId, int entryId, bool pinned, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var entry = await db.SearchHistory.FirstOrDefaultAsync(e => e.Id == entryId && e.UserId == userId, cancellationToken);
        if (entry is null)
        {
            return SearchHistoryWriteResult.NotFound;
        }

        if (pinned && !entry.IsPinned
            && await db.SearchHistory.CountAsync(e => e.UserId == userId && e.IsPinned, cancellationToken) >= ISearchHistoryStore.MaxPinned)
        {
            return SearchHistoryWriteResult.PinLimit;
        }

        entry.IsPinned = pinned;
        await db.SaveChangesAsync(cancellationToken);
        return SearchHistoryWriteResult.Ok;
    }

    /// <inheritdoc />
    public async Task<SearchHistoryWriteResult> DeleteAsync(int userId, int entryId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var deleted = await db.SearchHistory
            .Where(e => e.Id == entryId && e.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);
        return deleted == 0 ? SearchHistoryWriteResult.NotFound : SearchHistoryWriteResult.Ok;
    }

    /// <inheritdoc />
    public async Task<int> ClearAsync(int userId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.SearchHistory
            .Where(e => e.UserId == userId && !e.IsPinned)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
