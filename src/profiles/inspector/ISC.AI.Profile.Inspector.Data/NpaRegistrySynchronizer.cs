using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Ingestion;
using ISC.AI.Persistence;
using ISC.AI.Profile.Inspector.Domain.Entities;
using ISC.AI.Profile.Inspector.Domain.Enums;
using ISC.AI.Profile.Inspector.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Profile.Inspector.Data;

/// <summary>
/// Автонаполнение картотеки из корпуса (<see cref="INpaRegistrySynchronizer"/>): пачками обходит
/// документы корпуса без связки с картотекой, по метаданным ЦБД строит нормы/редакции/связки,
/// прежние автоматические редакции гасит материализатором (правила — в doc-комментарии порта).
/// </summary>
/// <remarks>
/// Метаданные корпуса — непрозрачный jsonb (ядро их не интерпретирует и в SQL по ним не фильтрует),
/// поэтому кандидаты читаются ПАЧКАМИ по номеру документа и разбираются в памяти: на корпусе 200К+
/// это десятки мегабайт суммарно, но не разом. Ключи <c>documentCode</c>/<c>editionId</c> обретают
/// смысл только здесь, в синхронизаторе профиля.
/// </remarks>
public sealed class NpaRegistrySynchronizer(
    IDbContextFactory<InspectorDbContext> inspectorFactory,
    IDbContextFactory<CoreDbContext> coreFactory,
    IRevisionStatusMaterializer materializer) : INpaRegistrySynchronizer
{
    private const int BatchSize = 500;

    /// <inheritdoc />
    public async Task<NpaSyncResult> SyncFromCorpusAsync(CancellationToken cancellationToken = default)
    {
        var scanned = 0;
        var normsCreated = 0;
        var revisionsCreated = 0;
        var documentsLinked = 0;
        var chunksLinked = 0;
        var revisionsRepealed = 0;

        // Уже привязанные документы — один раз в память (два контекста в одном SQL не совместить,
        // ТО-инф-06: разные схемы, слабые ссылки по значению). 200К int — единицы мегабайт.
        HashSet<int> linkedIds;
        await using (var inspector = await inspectorFactory.CreateDbContextAsync(cancellationToken))
        {
            linkedIds = (await inspector.NormDocumentLinks.AsNoTracking()
                .Select(l => l.DocumentId)
                .ToListAsync(cancellationToken)).ToHashSet();
        }

        var lastDocumentId = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Кандидаты: живые документы корпуса (не документооборот, не погашенные заменой).
            // Metadata материализуется — фильтр по ней ниже, в памяти.
            List<CandidateRow> batch;
            await using (var core = await coreFactory.CreateDbContextAsync(cancellationToken))
            {
                batch = await core.Documents.AsNoTracking()
                    .Where(d => d.Id > lastDocumentId
                        && d.Source != CorpusSources.DocFlow
                        && d.SupersededByDocumentId == null)
                    .OrderBy(d => d.Id)
                    .Take(BatchSize)
                    .Select(d => new CandidateRow(d.Id, d.Title, d.DocDate, d.Metadata))
                    .ToListAsync(cancellationToken);
            }

            if (batch.Count == 0)
            {
                break;
            }

            lastDocumentId = batch[^1].DocumentId;
            scanned += batch.Count;

            foreach (var candidate in batch)
            {
                if (linkedIds.Contains(candidate.DocumentId))
                {
                    continue; // уже в картотеке — идемпотентность.
                }

                cancellationToken.ThrowIfCancellationRequested();

                if (candidate.Metadata is not { } metadata
                    || metadata.GetValueOrDefault("documentCode") is not { Length: > 0 } documentCode)
                {
                    continue; // не из ЦБД (ручная загрузка) — картотеку по нему не строим.
                }

                var editionKey = metadata.GetValueOrDefault("editionId") is { Length: > 0 } editionId
                    ? editionId
                    // Без editionId ключ — номер документа корпуса: стабилен и идемпотентен.
                    : $"doc:{candidate.DocumentId.ToString(CultureInfo.InvariantCulture)}";

                var outcome = await ApplyAsync(candidate, documentCode, editionKey, cancellationToken);
                normsCreated += outcome.NormCreated ? 1 : 0;
                revisionsCreated += outcome.RevisionCreated ? 1 : 0;
                documentsLinked += outcome.DocumentLinked ? 1 : 0;
                chunksLinked += outcome.ChunksLinked;
                revisionsRepealed += outcome.RevisionsRepealed;
            }
        }

        return new NpaSyncResult(scanned, normsCreated, revisionsCreated, documentsLinked, chunksLinked, revisionsRepealed);
    }

    private async Task<(bool NormCreated, bool RevisionCreated, bool DocumentLinked, int ChunksLinked, int RevisionsRepealed)>
        ApplyAsync(CandidateRow candidate, string documentCode, string editionKey, CancellationToken cancellationToken)
    {
        await using var db = await inspectorFactory.CreateDbContextAsync(cancellationToken);

        // Норма — по стабильному коду ЦБД; гонка двух синхронизаций отбивается уникальным индексом.
        var norm = await db.LegalNorms.FirstOrDefaultAsync(n => n.Identifier == documentCode, cancellationToken);
        var normCreated = false;
        if (norm is null)
        {
            norm = new LegalNorm { Identifier = documentCode, Title = Truncate(candidate.Title, 1000) };
            db.LegalNorms.Add(norm);
            await db.SaveChangesAsync(cancellationToken);
            normCreated = true;
        }

        var revision = await db.NormRevisions
            .FirstOrDefaultAsync(r => r.NormId == norm.Id && r.ExternalEditionId == editionKey, cancellationToken);
        var revisionCreated = false;
        if (revision is null)
        {
            revision = new NormRevision
            {
                NormId = norm.Id,
                Status = RevisionStatus.Active,
                // Дата акта из заголовка (парсится при импорте); нет — день синхронизации, честнее пустоты.
                EffectiveDate = candidate.DocDate ?? DateOnly.FromDateTime(DateTime.UtcNow),
                ExternalEditionId = editionKey,
            };
            db.NormRevisions.Add(revision);
            await db.SaveChangesAsync(cancellationToken);
            revisionCreated = true;
        }

        // Связки: документ к норме, чанки документа — к редакции (гранула материализатора).
        var documentLinked = false;
        if (!await db.NormDocumentLinks.AnyAsync(
                l => l.LegalNormId == norm.Id && l.DocumentId == candidate.DocumentId, cancellationToken))
        {
            db.NormDocumentLinks.Add(new NormDocumentLink { LegalNormId = norm.Id, DocumentId = candidate.DocumentId });
            documentLinked = true;
        }

        List<int> chunkIds;
        await using (var core = await coreFactory.CreateDbContextAsync(cancellationToken))
        {
            chunkIds = await core.Chunks.AsNoTracking()
                .Where(c => c.DocumentId == candidate.DocumentId)
                .Select(c => c.Id)
                .ToListAsync(cancellationToken);
        }

        var alreadyLinked = await db.ChunkRevisionLinks.AsNoTracking()
            .Where(l => l.NormRevisionId == revision.Id && chunkIds.Contains(l.ChunkId))
            .Select(l => l.ChunkId)
            .ToListAsync(cancellationToken);
        var newChunkIds = chunkIds.Except(alreadyLinked).ToList();
        foreach (var chunkId in newChunkIds)
        {
            db.ChunkRevisionLinks.Add(new ChunkRevisionLink { NormRevisionId = revision.Id, ChunkId = chunkId });
        }

        await db.SaveChangesAsync(cancellationToken);

        // Новая редакция акта означает, что прежние АВТОМАТИЧЕСКИЕ редакции утратили силу (ЦБД отдаёт
        // только действующие). Гашение — материализатором (чанки скрываются, GATE-3, с его ограждениями).
        // Ручные редакции (ExternalEditionId == null) не трогаются: их статус решил человек.
        var revisionsRepealed = 0;
        if (revisionCreated)
        {
            var staleRevisionIds = await db.NormRevisions.AsNoTracking()
                .Where(r => r.NormId == norm.Id
                    && r.Id != revision.Id
                    && r.Status == RevisionStatus.Active
                    && r.ExternalEditionId != null)
                .Select(r => r.Id)
                .ToListAsync(cancellationToken);
            foreach (var staleRevisionId in staleRevisionIds)
            {
                await materializer.SetStatusAsync(staleRevisionId, RevisionStatus.Repealed, cancellationToken);
                revisionsRepealed++;
            }
        }

        return (normCreated, revisionCreated, documentLinked, newChunkIds.Count, revisionsRepealed);
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];

    private sealed record CandidateRow(
        int DocumentId, string Title, DateOnly? DocDate, Dictionary<string, string>? Metadata);
}
