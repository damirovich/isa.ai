using ISC.AI.Abstractions.Corpus;
using ISC.AI.Profile.Inspector.Domain.Enums;
using ISC.AI.Profile.Inspector.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Profile.Inspector.Data;

/// <summary>
/// Материализация статуса редакции НПА в нейтральный флаг годности ядра (Э4-02, ADR-0013):
/// по <c>ChunkRevisionLink</c> находит чанки редакции и через <see cref="IChunkCurrencyPort"/> ставит
/// их видимость, затем фиксирует доменный статус редакции.
/// </summary>
/// <remarks>
/// Порядок РЕЖИМНО-безопасный: сперва материализуем видимость в ядре (durable), затем сохраняем статус.
/// Для «утратила силу» это значит: даже если последующее сохранение статуса не удастся, материал уже
/// СКРЫТ (GATE-3 соблюдён) — это безопаснее единой транзакции, которая при откате оставила бы устаревшее видимым.
/// </remarks>
public sealed class RevisionStatusMaterializer(
    IDbContextFactory<InspectorDbContext> contextFactory,
    IChunkCurrencyPort chunkCurrencyPort) : IRevisionStatusMaterializer
{
    /// <inheritdoc />
    public async Task<int> SetStatusAsync(
        int normRevisionId, RevisionStatus status, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var revision = await db.NormRevisions.FirstOrDefaultAsync(r => r.Id == normRevisionId, cancellationToken)
            ?? throw new InvalidOperationException($"Редакция НПА {normRevisionId} не найдена.");

        var chunkIds = await db.ChunkRevisionLinks
            .Where(link => link.NormRevisionId == normRevisionId)
            .Select(link => link.ChunkId)
            .ToListAsync(cancellationToken);

        // Сперва — видимость в ядре (durable, режимно-безопасно): действующая видна, утратившая силу скрыта.
        await chunkCurrencyPort.SetCurrencyAsync(chunkIds, isCurrent: status == RevisionStatus.Active, cancellationToken);

        // Затем — доменный статус редакции.
        revision.Status = status;
        await db.SaveChangesAsync(cancellationToken);

        return chunkIds.Count;
    }
}
