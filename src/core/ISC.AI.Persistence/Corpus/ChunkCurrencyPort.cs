using ISC.AI.Abstractions.Corpus;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Persistence.Corpus;

/// <summary>
/// Реализация <see cref="IChunkCurrencyPort"/> на <see cref="CoreDbContext"/>: массово обновляет
/// <c>is_current</c> у чанков и их эмбеддингов в ОДНОЙ транзакции (чтобы флаг чанка и вектора не
/// разъезжались — это опора фильтра актуальности retrieval, GATE-3). Контекст — через фабрику (ТС-008).
/// </summary>
public sealed class ChunkCurrencyPort(IDbContextFactory<CoreDbContext> contextFactory) : IChunkCurrencyPort
{
    /// <inheritdoc />
    public async Task<int> SetCurrencyAsync(
        IReadOnlyCollection<int> chunkIds, bool isCurrent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chunkIds);
        if (chunkIds.Count == 0)
        {
            return 0;
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var affected = await db.Chunks
            .Where(chunk => chunkIds.Contains(chunk.Id))
            .ExecuteUpdateAsync(setters => setters.SetProperty(chunk => chunk.IsCurrent, isCurrent), cancellationToken);

        await db.Embeddings
            .Where(embedding => chunkIds.Contains(embedding.ChunkId))
            .ExecuteUpdateAsync(setters => setters.SetProperty(embedding => embedding.IsCurrent, isCurrent), cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return affected;
    }
}
