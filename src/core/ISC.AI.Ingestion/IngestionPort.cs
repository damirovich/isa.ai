using System.Security.Cryptography;
using System.Text;
using ISC.AI.Abstractions.AI;
using ISC.AI.Abstractions.Enums;
using ISC.AI.Abstractions.Ingestion;
using ISC.AI.Persistence;
using ISC.AI.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Pgvector;

namespace ISC.AI.Ingestion;

/// <summary>
/// Конвейер загрузки документов в корпус ядра (ТО-мат-03, ТО-инф-07). FAIL-CLOSED (ТБ-024): без явных
/// грифа и подразделения документ НЕ индексируется. Идемпотентность (ТНД-002): повтор того же содержимого
/// не создаёт дублей. Запись документа, чанков и эмбеддингов — в одной транзакции.
/// </summary>
/// <remarks>
/// Гриф/подразделение денормализуются на каждый чанк и эмбеддинг (опора фильтра доступа Э3-05, ТБ-020),
/// все новые фрагменты — актуальные (<c>IsCurrent = true</c>). Векторизация — отдельной моделью роли
/// <see cref="ModelRole.Embeddings"/>. Контекст создаётся через <c>IDbContextFactory</c> (ТС-008).
/// </remarks>
public sealed class IngestionPort(
    IDbContextFactory<CoreDbContext> contextFactory,
    [FromKeyedServices(ModelRole.Embeddings)] IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    ITextChunker chunker) : IIngestionPort
{
    /// <inheritdoc />
    public async Task<IngestionResult> IngestAsync(IngestionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // fail-closed (ТБ-024): гриф и подразделение обязательны и явные.
        if (request.Classification is not { } classification || request.DivisionId is not { } divisionId)
        {
            return IngestionResult.Reject("Не задан гриф или подразделение — индексация запрещена (fail-closed, ТБ-024).");
        }

        var contentHash = SHA256.HashData(Encoding.UTF8.GetBytes(request.Text ?? string.Empty));

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Идемпотентность (ТНД-002): тот же контент уже загружен — не дублируем.
        var existingId = await db.Documents
            .Where(d => d.ContentHash == contentHash)
            .Select(d => (int?)d.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (existingId is { } duplicateId)
        {
            return IngestionResult.Duplicate(duplicateId);
        }

        // Замена версии (Э4-14): заменяемый документ обязан существовать — иначе отказ (не молчаливо
        // «ничего не погасили», а явная ошибка оператору).
        if (request.SupersedesDocumentId is { } supersedesTargetId
            && !await db.Documents.AnyAsync(d => d.Id == supersedesTargetId, cancellationToken))
        {
            return IngestionResult.Reject($"Заменяемый документ #{supersedesTargetId} не найден — замена версии отменена.");
        }

        var chunkTexts = chunker.Chunk(request.Text ?? string.Empty);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var document = new DocumentEntity
        {
            DocType = request.DocType,
            Title = request.Title,
            Source = request.Source,
            DocDate = request.DocDate,
            StorageUri = request.StorageUri,
            ContentHash = contentHash,
            Classification = classification,
            DivisionId = divisionId,
        };
        db.Documents.Add(document);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Гонка дедупликации (ТНД-002): параллельный импорт того же содержимого выиграл вставку —
            // уникальный индекс content_hash отклонил дубль. Откатываемся и возвращаем «дубликат», как при
            // обычном дедупе (идемпотентность держится и под конкуренцией). Если документа с таким хешем
            // всё же нет — это иное нарушение целостности, не глотаем.
            await transaction.RollbackAsync(cancellationToken);
            var winnerId = await db.Documents.AsNoTracking()
                .Where(d => d.ContentHash == contentHash)
                .Select(d => (int?)d.Id)
                .FirstOrDefaultAsync(cancellationToken);
            if (winnerId is { } raceWinnerId)
            {
                return IngestionResult.Duplicate(raceWinnerId);
            }

            throw;
        }

        // Замена версии (Э4-14): ПЕРЕД добавлением новых чанков гасим прежнюю версию (hide-first) — её
        // чанки и эмбеддинги становятся неактуальными в ЭТОЙ ЖЕ транзакции (атомарно, опора GATE-3:
        // в ИИ/поиск уходит только новая версия, старая и новая одновременно current не бывают).
        var supersededChunkCount = 0;
        if (request.SupersedesDocumentId is { } supersededId)
        {
            var oldChunkIds = await db.Chunks
                .Where(c => c.DocumentId == supersededId)
                .Select(c => c.Id)
                .ToListAsync(cancellationToken);

            if (oldChunkIds.Count > 0)
            {
                await db.Chunks.Where(c => oldChunkIds.Contains(c.Id))
                    .ExecuteUpdateAsync(s => s.SetProperty(c => c.IsCurrent, false), cancellationToken);
                await db.Embeddings.Where(e => oldChunkIds.Contains(e.ChunkId))
                    .ExecuteUpdateAsync(s => s.SetProperty(e => e.IsCurrent, false), cancellationToken);
            }

            supersededChunkCount = oldChunkIds.Count;

            // Пометить прежнюю версию заменённой (история версий; сама строка документа сохраняется).
            await db.Documents.Where(d => d.Id == supersededId)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.SupersededByDocumentId, (int?)document.Id), cancellationToken);
        }

        // Итог успеха: с инфо о замене, если это была новая версия.
        IngestionResult Success(int chunkCount) =>
            request.SupersedesDocumentId is { } sid
                ? IngestionResult.Ok(document.Id, chunkCount, sid, supersededChunkCount)
                : IngestionResult.Ok(document.Id, chunkCount);

        if (chunkTexts.Count == 0)
        {
            await transaction.CommitAsync(cancellationToken);
            return Success(0);
        }

        var chunks = new List<ChunkEntity>(chunkTexts.Count);
        for (var ordinal = 0; ordinal < chunkTexts.Count; ordinal++)
        {
            chunks.Add(new ChunkEntity
            {
                DocumentId = document.Id,
                Ordinal = ordinal,
                Text = chunkTexts[ordinal],
                Classification = classification,
                DivisionId = divisionId,
                IsCurrent = true,
            });
        }

        db.Chunks.AddRange(chunks);
        await db.SaveChangesAsync(cancellationToken);

        // Каждый чанк оборачивается document-префиксом: EmbeddingGemma кодирует документ и запрос
        // асимметрично, иначе retrieval рассогласован (Э4-09, ADR-0011). Порядок входов = порядок чанков.
        var embeddingInputs = new string[chunkTexts.Count];
        for (var i = 0; i < chunkTexts.Count; i++)
        {
            embeddingInputs[i] = EmbeddingTaskPrompt.Document(chunkTexts[i]);
        }

        var embeddings = await embeddingGenerator.GenerateAsync(embeddingInputs, cancellationToken: cancellationToken);
        for (var i = 0; i < chunks.Count; i++)
        {
            db.Embeddings.Add(new EmbeddingEntity
            {
                ChunkId = chunks[i].Id,
                Embedding = new Vector(embeddings[i].Vector),
                ModelKey = ModelRole.Embeddings.ToString(),
                Classification = classification,
                DivisionId = divisionId,
                IsCurrent = true,
            });
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return Success(chunks.Count);
    }
}
