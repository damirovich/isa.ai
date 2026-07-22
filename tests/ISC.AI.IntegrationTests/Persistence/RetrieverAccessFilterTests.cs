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
/// GATE-1 (ТБ-020/021/022): фильтр доступа на стороне БД. Материал выше допуска, из чужого
/// подразделения и утративший силу не выдаётся; запрос без подходящего допуска возвращает ПУСТО —
/// неотличимо от «документ отсутствует» (по контенту). Реальный PostgreSQL+pgvector через Testcontainers.
/// </summary>
/// <remarks>
/// Требуется Docker. Эмбеддер — фиксированный фейк (тест проверяет ФИЛЬТРАЦИЮ, не качество векторов).
/// Неразличимость по времени/тексту ошибки (timing/oracle) — отдельная методика (Э3-09), здесь не покрыта.
/// </remarks>
[Trait("Category", "Gate")]
public sealed class RetrieverAccessFilterTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("pgvector/pgvector:pg16").Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "GATE-1: выше допуска / чужое подразделение / устаревшее не выдаётся; нет доступа = пусто")]
    public async Task Retriever_enforces_access_filter_on_db_side()
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
            new AllowAllAccessPolicy(),
            RetrievalOptions.None);

        // Субъект: допуск гриф ≤ 1, подразделение 7.
        var access = new AccessContext("u1", MaxClassification: 1, AllowedDivisions: [7]);
        var results = await retriever.RetrieveAsync("любой запрос", access, topK: 50);

        // Возвращается ТОЛЬКО допустимый актуальный фрагмент (cls=1, div=7, current).
        results.Count.ShouldBe(1);
        results[0].Classification.ShouldBe<short>(1);
        results[0].DivisionId.ShouldBe(7);
        results[0].IsCurrent.ShouldBeTrue();

        // Неразличимость (по контенту): субъект без подходящего допуска получает ПУСТО.
        var noAccess = new AccessContext("u2", MaxClassification: 0, AllowedDivisions: [999]);
        (await retriever.RetrieveAsync("любой запрос", noAccess, topK: 50)).ShouldBeEmpty();
    }

    private static async Task SeedAsync(CoreDbContext db)
    {
        var doc = new DocumentEntity { DocType = "приказ", Title = "Тест", Classification = 1, DivisionId = 7 };
        db.Documents.Add(doc);
        await db.SaveChangesAsync();

        await AddChunkWithEmbeddingAsync(db, doc.Id, ordinal: 0, classification: 1, divisionId: 7, isCurrent: true);   // допустимо
        await AddChunkWithEmbeddingAsync(db, doc.Id, ordinal: 1, classification: 2, divisionId: 7, isCurrent: true);   // выше допуска
        await AddChunkWithEmbeddingAsync(db, doc.Id, ordinal: 2, classification: 1, divisionId: 9, isCurrent: true);   // чужое подразделение
        await AddChunkWithEmbeddingAsync(db, doc.Id, ordinal: 3, classification: 1, divisionId: 7, isCurrent: false);  // утратило силу
    }

    private static async Task AddChunkWithEmbeddingAsync(
        CoreDbContext db, int documentId, int ordinal, short classification, int divisionId, bool isCurrent)
    {
        var chunk = new ChunkEntity
        {
            DocumentId = documentId,
            Ordinal = ordinal,
            Text = $"фрагмент {ordinal}",
            Classification = classification,
            DivisionId = divisionId,
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
            Classification = classification,
            DivisionId = divisionId,
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

    // Фейковый эмбеддер: всегда один и тот же вектор (тест проверяет фильтрацию, не качество поиска).
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
