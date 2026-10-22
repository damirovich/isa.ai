namespace ISC.AI.Persistence.Security;

/// <summary>
/// Учёт сеансов входа поверх <c>core.user_session</c> (реализация <see cref="IUserSessionStore"/>, ADR-0060). Контекст —
/// на операцию, через фабрику (ТС-008); обновления — <c>ExecuteUpdateAsync</c> без загрузки строки. Регистрируется как
/// singleton: им пользуются проверка cookie и реестр соединений хоста.
/// </summary>
public sealed class UserSessionStore(IDbContextFactory<CoreDbContext> contextFactory) : IUserSessionStore
{
    /// <inheritdoc />
    public async Task<Guid> StartAsync(UserSessionStart start, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(start);

        var now = DateTime.UtcNow;
        var session = new UserSessionEntity
        {
            Id = Guid.NewGuid(),
            UserId = start.UserId,
            ClientAddress = Trim(start.ClientAddress, UserSessionEntity.MaxClientAddressLength),
            UserAgent = Trim(start.UserAgent, UserSessionEntity.MaxUserAgentLength),
            StartedAt = now,
            LastActivityAt = now,
        };

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        db.UserSessions.Add(session);
        await db.SaveChangesAsync(cancellationToken);
        return session.Id;
    }

    /// <inheritdoc />
    public async Task<bool> IsActiveAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.UserSessions.AnyAsync(s => s.Id == sessionId && s.EndedAt == null, cancellationToken);
    }

    /// <inheritdoc />
    public async Task TouchAsync(Guid sessionId, DateTime atUtc, CancellationToken cancellationToken = default)
    {
        // Только вперёд: запоздавшая отметка не откатывает время активности назад.
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await db.UserSessions
            .Where(s => s.Id == sessionId && s.EndedAt == null && s.LastActivityAt < atUtc)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.LastActivityAt, atUtc), cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> EndAsync(
        Guid sessionId, UserSessionEndReason reason, int? endedByUserId, string? comment, CancellationToken cancellationToken = default)
    {
        // ИНВАРИАНТ (ТБ-016): закрывается только действующий сеанс — одним условным UPDATE, без гонки «прочитал →
        // записал»: из двух одновременных завершений сработает одно, второе вернёт false, причина первого не затрётся.
        var trimmed = Trim(comment, UserSessionEntity.MaxEndCommentLength);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var changed = await db.UserSessions
            .Where(s => s.Id == sessionId && s.EndedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.EndedAt, DateTime.UtcNow)
                .SetProperty(e => e.EndReason, reason)
                .SetProperty(e => e.EndedByUserId, endedByUserId)
                .SetProperty(e => e.EndComment, trimmed), cancellationToken);
        return changed > 0;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<UserSessionRow>> ListOpenAsync(
        DateTime activeSinceUtc, IReadOnlyCollection<Guid> connected, string? text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connected);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var connectedIds = connected.ToList();

        // Учётки — БЕЗ фильтра мягкого удаления: открытый сеанс удалённой учётки показывается, его как раз и нужно
        // уметь завершить. У самих сеансов фильтров нет, поэтому снятие фильтров запроса ничего другого не открывает.
        var query =
            from s in db.UserSessions.AsNoTracking()
            join u in db.Users.AsNoTracking() on s.UserId equals u.Id
            where s.EndedAt == null && (s.LastActivityAt >= activeSinceUtc || connectedIds.Contains(s.Id))
            select new { Session = s, u.UserName, u.DisplayName };

        if (!string.IsNullOrWhiteSpace(text))
        {
            // Символы шаблона LIKE в тексте пользователя — буквальные (как в журнале аудита).
            var pattern = "%" + EscapeLike(text.Trim()) + "%";
            query = query.Where(r =>
                EF.Functions.ILike(r.UserName, pattern, "\\")
                || (r.DisplayName != null && EF.Functions.ILike(r.DisplayName, pattern, "\\"))
                || (r.Session.ClientAddress != null && EF.Functions.ILike(r.Session.ClientAddress, pattern, "\\")));
        }

        return await query
            .IgnoreQueryFilters()
            .OrderByDescending(r => r.Session.LastActivityAt)
            .Select(r => new UserSessionRow(
                r.Session.Id, r.Session.UserId, r.UserName, r.DisplayName, r.Session.ClientAddress, r.Session.UserAgent,
                r.Session.StartedAt, r.Session.LastActivityAt))
            .ToListAsync(cancellationToken);
    }

    private static string? Trim(string? value, int maxLength) =>
        string.IsNullOrWhiteSpace(value) ? null
        : value.Trim() is { } trimmed && trimmed.Length > maxLength ? trimmed[..maxLength]
        : value.Trim();

    private static string EscapeLike(string text) =>
        text.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
}
