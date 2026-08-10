using ISC.AI.AI.Security;
using ISC.AI.Abstractions.AI;
using ISC.AI.Abstractions.Enums;
using ISC.AI.Abstractions.Retrieval;
using ISC.AI.Abstractions.Security;
using ISC.AI.Persistence;
using ISC.AI.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace ISC.AI.AI.Retrieval;

/// <summary>
/// Извлечение фрагментов из корпуса ядра через pgvector (ТО-мат-01). Фильтр доступа применяется
/// НА СТОРОНЕ БД как PRE-FILTER (ADR-0007, ТБ-020): множество кандидатов ANN-поиска ограничивается
/// допуском субъекта ДО ранжирования.
/// </summary>
/// <remarks>
/// FAIL-CLOSED (ТБ-012/021): без контекста доступа извлечение не выполняется. Floor доступа
/// (<see cref="BaselineAccess"/>) применяется ВСЕГДА и профилем не отключается; профильная
/// <see cref="IAccessPolicy"/> может только СУЖАТЬ (ADR-0014). По умолчанию возвращаются только
/// актуальные источники (<c>IsCurrent</c>, ADR-0013). Эмбеддинг запроса — отдельной моделью роли
/// <see cref="ModelRole.Embeddings"/> (ADR-0011). Контекст создаётся через IDbContextFactory (ТС-008).
/// </remarks>
public sealed class PgVectorRetriever(
    IDbContextFactory<CoreDbContext> contextFactory,
    [FromKeyedServices(ModelRole.Embeddings)] IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
    IAccessPolicy accessPolicy,
    RetrievalOptions options) : IRetriever
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<RetrievedChunk>> RetrieveAsync(
        string query,
        AccessContext access,
        int topK,
        RetrievalFilter? filter = null,
        CancellationToken cancellationToken = default)
    {
        // fail-closed: без субъекта с допуском извлечение невозможно (ТБ-012/021).
        if (access is null)
        {
            throw new AccessContextRequiredException();
        }

        // Векторизация запроса отдельной моделью эмбеддингов (роль Embeddings). Запрос оборачивается
        // query-префиксом: EmbeddingGemma кодирует запрос и документ асимметрично (Э4-09, ADR-0011).
        var embeddings = await embeddingGenerator.GenerateAsync(
            [EmbeddingTaskPrompt.Query(query)], cancellationToken: cancellationToken);
        var queryVector = new Vector(embeddings.Single().Vector);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        // PRE-FILTER (ТБ-020): floor ядра (гриф ≤ допуск ∧ подразделение ∈ разрешённых) + сужение профиля.
        IQueryable<EmbeddingEntity> candidates = db.Embeddings
            .Where(BaselineAccess.Filter<EmbeddingEntity>(access))
            .Where(accessPolicy.BuildFilter<EmbeddingEntity>(access));

        // По умолчанию — только актуальные источники (ADR-0013); устаревшие — лишь по явному запросу.
        if (filter is null || !filter.IncludeSuperseded)
        {
            candidates = candidates.Where(e => e.IsCurrent);
        }

        // Дистанция ВЫБРАННОЙ метрикой (ТО-мат-04: ранжирование задаётся конфигурацией) — вычисляется ОДИН
        // раз проекцией, чтобы порог, сортировка и Score использовали одну метрику. По умолчанию Cosine
        // (совпадает с HNSW-индексом); иная метрика ранжирует корректно, но индекс к ней не подходит.
        var scored = options.Metric switch
        {
            RetrievalMetric.Euclidean =>
                candidates.Select(e => new { Embedding = e, Distance = e.Embedding.L2Distance(queryVector) }),
            RetrievalMetric.NegativeInnerProduct =>
                candidates.Select(e => new { Embedding = e, Distance = e.Embedding.MaxInnerProduct(queryVector) }),
            _ => candidates.Select(e => new { Embedding = e, Distance = e.Embedding.CosineDistance(queryVector) }),
        };

        // Порог отсечения по релевантности (ТО-мат-04) — ПОСЛЕ фильтра доступа, не вместо него: сужает
        // выдачу по качеству совпадения (в единицах метрики), topK может быть не исчерпан. Не задан — нет отсечения.
        if (options.MaxDistance is { } maxDistance)
        {
            scored = scored.Where(s => s.Distance <= maxDistance);
        }

        return await scored
            .OrderBy(s => s.Distance)
            .Take(topK)
            .Select(s => new RetrievedChunk(
                s.Embedding.ChunkId,
                s.Embedding.Chunk!.DocumentId,
                s.Embedding.Chunk.Text,
                s.Embedding.Classification,
                s.Embedding.DivisionId,
                s.Embedding.IsCurrent,
                s.Distance,
                s.Embedding.Chunk.Document!.Metadata))
            .ToListAsync(cancellationToken);
    }
}
