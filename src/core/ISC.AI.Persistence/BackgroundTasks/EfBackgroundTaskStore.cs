using ISC.AI.Abstractions.BackgroundTasks;

namespace ISC.AI.Persistence.BackgroundTasks;

/// <summary>
/// EF-персист статусов фоновых задач (Э4-20, схема <c>core</c>). Контекст — через
/// <see cref="IDbContextFactory{T}"/> на операцию (ТС-008). Обновления — <c>ExecuteUpdateAsync</c>
/// (без загрузки сущности). Регистрируется как singleton (фабрика создаёт контекст на вызов).
/// </summary>
public sealed class EfBackgroundTaskStore(IDbContextFactory<CoreDbContext> contextFactory) : IBackgroundTaskStore
{
    /// <inheritdoc />
    public async Task CreateAsync(Guid id, string kind, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        db.BackgroundTasks.Add(new BackgroundTaskEntity
        {
            Id = id,
            Kind = kind,
            Status = BackgroundTaskStatus.Queued,
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task MarkRunningAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await db.BackgroundTasks.Where(t => t.Id == id).ExecuteUpdateAsync(s => s
            .SetProperty(t => t.Status, BackgroundTaskStatus.Running)
            .SetProperty(t => t.StartedAt, DateTime.UtcNow), cancellationToken);
    }

    /// <inheritdoc />
    public async Task MarkCompletedAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await db.BackgroundTasks.Where(t => t.Id == id).ExecuteUpdateAsync(s => s
            .SetProperty(t => t.Status, BackgroundTaskStatus.Completed)
            .SetProperty(t => t.FinishedAt, DateTime.UtcNow), cancellationToken);
    }

    /// <inheritdoc />
    public async Task MarkFailedAsync(Guid id, string errorMessage, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await db.BackgroundTasks.Where(t => t.Id == id).ExecuteUpdateAsync(s => s
            .SetProperty(t => t.Status, BackgroundTaskStatus.Failed)
            .SetProperty(t => t.FinishedAt, DateTime.UtcNow)
            .SetProperty(t => t.Error, errorMessage), cancellationToken);
    }

    /// <inheritdoc />
    public async Task<BackgroundTaskInfo?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.BackgroundTasks
            .Where(t => t.Id == id)
            .Select(t => new BackgroundTaskInfo(
                t.Id, t.Kind, t.Status, t.CreatedAt, t.StartedAt, t.FinishedAt, t.Error))
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int> RecoverOrphanedAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Незавершённые (Queued/Running) → Failed: делегаты потеряны с очередью в памяти (§5.1.4.5).
        return await db.BackgroundTasks
            .Where(t => t.Status == BackgroundTaskStatus.Queued || t.Status == BackgroundTaskStatus.Running)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Status, BackgroundTaskStatus.Failed)
                .SetProperty(t => t.FinishedAt, DateTime.UtcNow)
                .SetProperty(t => t.Error, "Прервано перезапуском системы."), cancellationToken);
    }
}
