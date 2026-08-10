using ISC.AI.Abstractions.Corpus;
using ISC.AI.Persistence;
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
///
/// Два ограждения (найдены ревью картотеки, 2026-08-10), оба — в сторону «скрыть надёжнее, чем показать»:
/// <list type="number">
/// <item>ПОДНЯТИЕ (Active) не касается чанков документов, погашенных заменой на уровне корпуса
/// (<c>SupersededByDocumentId</c>, Э4-14): возврат редакции в «действующие» не должен воскрешать
/// текст, который сам корпус уже заменил новой версией.</item>
/// <item>ГАШЕНИЕ (Repealed) не касается чанков, привязанных к ДРУГОЙ действующей редакции (общий
/// документ двух норм): утрата силы одной нормы не должна прятать материал, действующий по другой.</item>
/// </list>
/// </remarks>
public sealed class RevisionStatusMaterializer(
    IDbContextFactory<InspectorDbContext> contextFactory,
    IDbContextFactory<CoreDbContext> coreFactory,
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

        if (status == RevisionStatus.Active)
        {
            // Ограждение 1: не воскрешать чанки документов, погашенных заменой в самом корпусе.
            if (chunkIds.Count > 0)
            {
                await using var core = await coreFactory.CreateDbContextAsync(cancellationToken);
                chunkIds = await core.Chunks.AsNoTracking()
                    .Where(c => chunkIds.Contains(c.Id) && c.Document!.SupersededByDocumentId == null)
                    .Select(c => c.Id)
                    .ToListAsync(cancellationToken);
            }
        }
        else if (chunkIds.Count > 0)
        {
            // Ограждение 2: не гасить чанки, действующие по другой редакции (общий документ двух норм).
            var protectedIds = await db.ChunkRevisionLinks
                .Where(link => link.NormRevisionId != normRevisionId
                    && chunkIds.Contains(link.ChunkId)
                    && link.Revision!.Status == RevisionStatus.Active)
                .Select(link => link.ChunkId)
                .ToListAsync(cancellationToken);
            chunkIds = [.. chunkIds.Except(protectedIds)];
        }

        // Сперва — видимость в ядре (durable, режимно-безопасно): действующая видна, утратившая силу скрыта.
        await chunkCurrencyPort.SetCurrencyAsync(chunkIds, isCurrent: status == RevisionStatus.Active, cancellationToken);

        // Затем — доменный статус редакции. Дата утраты силы проставляется здесь же — карточка её
        // показывает, а больше её не заполняет никто.
        revision.Status = status;
        revision.RepealedDate = status == RevisionStatus.Repealed
            ? DateOnly.FromDateTime(DateTime.UtcNow)
            : null;
        await db.SaveChangesAsync(cancellationToken);

        return chunkIds.Count;
    }
}
