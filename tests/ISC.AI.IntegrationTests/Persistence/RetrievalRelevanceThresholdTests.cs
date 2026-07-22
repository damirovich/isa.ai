using ISC.AI.AI.Retrieval;
using ISC.AI.AI.Security;
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
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("pgvector/pgvector:pg16").Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "ТО-мат-04: MaxDistance отсекает дальний фрагмент; без порога отдаются оба")]
    public async Task MaxDistance_excludes_far_fragment_only_when_configured()
    {
        var factory = new TestContextFactory(_postgres.GetConnectionString());
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

    // Контекст с теми же опциями, что в проде (snake_case + pgvector).
    private sealed class TestContextFactory(string connectionString) : IDbContextFactory<CoreDbContext>
    {
        public CoreDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<CoreDbContext>()
                .UseNpgsql(connectionString, npg =>
                {
                    npg.MigrationsHistoryTable("__ef_migrations_history", CoreDbContext.Schema);
                    npg.UseVector();
                })
                .UseSnakeCaseNamingConvention()
                .Options);
    }

    // Фейковый эмбеддер запроса: всегда вектор values[0]=1 — совпадает с «релевантным» фрагментом.
    private sealed class FixedEmbeddingGenerator(int dimensions) : IEmbeddingGenerator<string, Embedding<float>>
    {
        private readonly ReadOnlyMemory<float> _vector = BuildVector(dimensions);

        private static float[] BuildVector(int dimensions)
        {
            var values = new float[dimensions];
            values[0] = 1f;
            return values;
        }

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(
                values.Select(_ => new Embedding<float>(_vector)).ToList()));

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
