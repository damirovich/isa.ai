using ISC.AI.AI.Retrieval;
using ISC.AI.AI.Security;
using ISC.AI.Abstractions.Retrieval;
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
/// GATE-3 (КИ-02, ТЭ-003, ADR-0013): версионность редакций. Утратившая силу редакция
/// (<c>IsCurrent = false</c>) НЕ выдаётся как действующая по умолчанию (КИ-02 — доля
/// «устаревшее-как-актуальное» = 0); неактуальные источники включаются ТОЛЬКО явным
/// <see cref="RetrievalFilter.IncludeSuperseded"/> — для показа с пометкой «утратила силу» (ТЭ-003).
/// Реальный PostgreSQL+pgvector через Testcontainers.
/// </summary>
/// <remarks>
/// Требуется Docker. Эмбеддер — фиксированный фейк (тест проверяет ВИДИМОСТЬ ПО РЕДАКЦИИ, не качество векторов).
/// </remarks>
[Trait("Category", "Gate")]
public sealed class Gate3RevisionVisibilityTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("pgvector/pgvector:pg16").Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "GATE-3: утратившая силу не выдаётся как действующая; видна только при IncludeSuperseded")]
    public async Task Superseded_revision_is_not_served_as_current()
    {
        var factory = new TestContextFactory(_postgres.GetConnectionString());
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
            await SeedAsync(db);
        }

        var retriever = new PgVectorRetriever(
            factory,
            new FixedEmbeddingGenerator(EmbeddingEntity.Dimensions),
            new AllowAllAccessPolicy());

        var access = new AccessContext("u1", MaxClassification: 0, AllowedDivisions: [7]);

        // По умолчанию: только действующая редакция (КИ-02 — устаревшее как актуальное не выдаётся).
        var current = await retriever.RetrieveAsync("любой запрос", access, topK: 50);
        current.Count.ShouldBe(1);
        current.ShouldAllBe(c => c.IsCurrent);

        // Явный показ утративших силу (ТЭ-003): обе редакции; неактуальная помечена IsCurrent=false.
        var withStale = await retriever.RetrieveAsync(
            "любой запрос", access, topK: 50, filter: new RetrievalFilter(IncludeSuperseded: true));
        withStale.Count.ShouldBe(2);
        withStale.ShouldContain(c => c.IsCurrent);
        withStale.ShouldContain(c => !c.IsCurrent);
    }

    private static async Task SeedAsync(CoreDbContext db)
    {
        var doc = new DocumentEntity { DocType = "положение", Title = "Положение о порядке", Classification = 0, DivisionId = 7 };
        db.Documents.Add(doc);
        await db.SaveChangesAsync();

        // Две редакции одного материала: действующая и утратившая силу (тот же вектор).
        await AddChunkWithEmbeddingAsync(db, doc.Id, ordinal: 0, isCurrent: true);
        await AddChunkWithEmbeddingAsync(db, doc.Id, ordinal: 1, isCurrent: false);
    }

    private static async Task AddChunkWithEmbeddingAsync(CoreDbContext db, int documentId, int ordinal, bool isCurrent)
    {
        var chunk = new ChunkEntity
        {
            DocumentId = documentId,
            Ordinal = ordinal,
            Text = $"редакция {ordinal}",
            Classification = 0,
            DivisionId = 7,
            IsCurrent = isCurrent,
        };
        db.Chunks.Add(chunk);
        await db.SaveChangesAsync();

        var values = new float[EmbeddingEntity.Dimensions];
        values[0] = 1f; // ненулевой вектор — косинусное расстояние определено
        db.Embeddings.Add(new EmbeddingEntity
        {
            ChunkId = chunk.Id,
            Embedding = new Vector(values),
            ModelKey = "test",
            Classification = 0,
            DivisionId = 7,
            IsCurrent = isCurrent,
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

    // Фейковый эмбеддер: всегда один и тот же вектор (тест проверяет видимость по редакции, не качество поиска).
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
