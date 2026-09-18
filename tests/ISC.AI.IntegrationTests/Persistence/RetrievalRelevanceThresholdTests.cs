using ISC.AI.AI.Retrieval;
using ISC.AI.Abstractions.Security;
using ISC.AI.Persistence;
using ISC.AI.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using Shouldly;
using Testcontainers.PostgreSql;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// ТО-мат-04: порог отсечения по релевантности (<see cref="RetrievalOptions.MaxDistance"/>) исключает
/// фрагменты, косинусное расстояние которых до запроса больше настроенного порога, даже если <c>topK</c>
/// ещё не исчерпан. Без порога (<see cref="RetrievalOptions.None"/>) поведение прежнее — top-K как есть.
/// Реальный PostgreSQL+pgvector через Testcontainers.
/// </summary>
/// <remarks>
/// Требуется Docker. Эмбеддер запроса — фиксированный фейк; у фрагментов — заранее известные векторы
/// (совпадающий и ортогональный запросу), чтобы расстояние было детерминированным (0 и 1), а не зависело
/// от качества реальной модели эмбеддингов.
/// </remarks>
[Trait("Category", "Gate")]
public sealed class RetrievalRelevanceThresholdTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = TestPostgres.Create();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "ТО-мат-04: MaxDistance отсекает дальний фрагмент; без порога отдаются оба")]
    public async Task MaxDistance_excludes_far_fragment_only_when_configured()
    {
        var factory = new CoreContextFactory(_postgres.GetConnectionString());
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
            await SeedAsync(db);
        }

        var access = new AccessContext("u1", MaxClassification: 0, AllowedDivisions: [7]);
        var embeddingGenerator = new FixedEmbeddingGenerator(EmbeddingEntity.Dimensions);

        // Строгий порог (< 1): отсекает ортогональный фрагмент (расстояние 1), оставляет совпадающий (0).
        var strict = new PgVectorRetriever(
            factory, embeddingGenerator, new AllowAllAccessPolicy(), new RetrievalOptions(MaxDistance: 0.5));
        var strictResults = await strict.RetrieveAsync("любой запрос", access, topK: 50);
        strictResults.Count.ShouldBe(1);
        strictResults[0].Text.ShouldBe("релевантный");

        // Без порога (дефолт) — фильтрации по расстоянию нет, topK не урезан заранее обоими фрагментами.
        var unbounded = new PgVectorRetriever(
            factory, embeddingGenerator, new AllowAllAccessPolicy(), RetrievalOptions.None);
        var unboundedResults = await unbounded.RetrieveAsync("любой запрос", access, topK: 50);
        unboundedResults.Count.ShouldBe(2);
    }

    [Fact(DisplayName = "ТО-мат-04: метрика ранжирования из конфигурации применяется (Euclidean ≠ Cosine на тех же данных)")]
    public async Task Configured_metric_is_applied_to_ranking()
    {
        var factory = new CoreContextFactory(_postgres.GetConnectionString());
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();

            var doc = new DocumentEntity { DocType = "приказ", Title = "Тест", Classification = 0, DivisionId = 7 };
            db.Documents.Add(doc);
            await db.SaveChangesAsync();

            var chunk = new ChunkEntity
            {
                DocumentId = doc.Id, Ordinal = 0, Text = "фрагмент", Classification = 0, DivisionId = 7, IsCurrent = true,
            };
            db.Chunks.Add(chunk);
            await db.SaveChangesAsync();

            // Та же НАПРАВЛЕННОСТЬ, что и вектор запроса [1,0,…], но втрое длиннее: cosine-дистанция 0, L2 = |3−1| = 2.
            var values = new float[EmbeddingEntity.Dimensions];
            values[0] = 3f;
            db.Embeddings.Add(new EmbeddingEntity
            {
                ChunkId = chunk.Id, Embedding = new Vector(values), ModelKey = "test",
                Classification = 0, DivisionId = 7, IsCurrent = true,
            });
            await db.SaveChangesAsync();
        }

        var access = new AccessContext("u1", MaxClassification: 0, AllowedDivisions: [7]);
        var embeddingGenerator = new FixedEmbeddingGenerator(EmbeddingEntity.Dimensions); // вектор запроса [1,0,…]

        // Cosine (по умолчанию): совпадение по направлению → расстояние ≈ 0.
        var cosine = new PgVectorRetriever(
            factory, embeddingGenerator, new AllowAllAccessPolicy(), new RetrievalOptions(Metric: RetrievalMetric.Cosine));
        (await cosine.RetrieveAsync("запрос", access, topK: 1))[0].Score.ShouldBe(0.0, tolerance: 1e-4);

        // Euclidean (из конфигурации): та же пара даёт L2 = 2 — метрика реально применена, а не зашита.
        var euclidean = new PgVectorRetriever(
            factory, embeddingGenerator, new AllowAllAccessPolicy(), new RetrievalOptions(Metric: RetrievalMetric.Euclidean));
        (await euclidean.RetrieveAsync("запрос", access, topK: 1))[0].Score.ShouldBe(2.0, tolerance: 1e-4);
    }

    private static async Task SeedAsync(CoreDbContext db)
    {
        var doc = new DocumentEntity { DocType = "приказ", Title = "Тест", Classification = 0, DivisionId = 7 };
        db.Documents.Add(doc);
        await db.SaveChangesAsync();

        // Совпадает с вектором запроса (values[0]=1) — косинусное расстояние 0.
        await AddChunkWithEmbeddingAsync(db, doc.Id, ordinal: 0, text: "релевантный", axis: 0);
        // Ортогонален вектору запроса (values[1]=1) — косинусное расстояние 1.
        await AddChunkWithEmbeddingAsync(db, doc.Id, ordinal: 1, text: "нерелевантный", axis: 1);
    }

    private static async Task AddChunkWithEmbeddingAsync(
        CoreDbContext db, int documentId, int ordinal, string text, int axis)
    {
        var chunk = new ChunkEntity
        {
            DocumentId = documentId,
            Ordinal = ordinal,
            Text = text,
            Classification = 0,
            DivisionId = 7,
            IsCurrent = true,
        };
        db.Chunks.Add(chunk);
        await db.SaveChangesAsync();

        var values = new float[EmbeddingEntity.Dimensions];
        values[axis] = 1f;
        db.Embeddings.Add(new EmbeddingEntity
        {
            ChunkId = chunk.Id,
            Embedding = new Vector(values),
            ModelKey = "test",
            Classification = 0,
            DivisionId = 7,
            IsCurrent = true,
        });
        await db.SaveChangesAsync();
    }
}
