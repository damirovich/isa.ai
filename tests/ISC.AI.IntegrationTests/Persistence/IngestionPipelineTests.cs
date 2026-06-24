using ISC.AI.Abstractions.Ingestion;
using ISC.AI.Ingestion;
using ISC.AI.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Pgvector.EntityFrameworkCore;
using Shouldly;
using Testcontainers.PostgreSql;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Интеграционные тесты загрузки (ТБ-024, ТНД-002) на настоящем PostgreSQL+pgvector: документ с грифом
/// индексируется (документ/чанки/эмбеддинги — все с грифом); повтор того же содержимого не создаёт
/// дублей; документ без грифа отклоняется и в индекс не попадает.
/// </summary>
/// <remarks>Требуется Docker. Эмбеддер — фиксированный фейк (проверяется конвейер/инвариант, не качество).</remarks>
public sealed class IngestionPipelineTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("pgvector/pgvector:pg16").Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "Загрузка с грифом индексирует документ/чанки/эмбеддинги; повтор без дублей; без грифа — отказ")]
    public async Task Ingestion_is_fail_closed_and_idempotent()
    {
        var factory = new TestContextFactory(_postgres.GetConnectionString());
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        var port = new IngestionPort(factory, new FixedEmbeddingGenerator(768), new SimpleTextChunker());

        const string text = "Первый абзац документа.\n\nВторой абзац документа.";
        var request = new IngestionRequest("приказ", "Тестовый документ", text, Classification: 1, DivisionId: 7);

        // 1) Загрузка с грифом — принята и проиндексирована.
        var first = await port.IngestAsync(request);
        first.Accepted.ShouldBeTrue();
        first.DocumentId.ShouldNotBeNull();
        first.ChunkCount.ShouldBeGreaterThan(0);

        await using (var db = factory.CreateDbContext())
        {
            (await db.Documents.CountAsync()).ShouldBe(1);
            (await db.Chunks.CountAsync()).ShouldBe(first.ChunkCount);
            (await db.Embeddings.CountAsync()).ShouldBe(first.ChunkCount);
            // Каждый чанк несёт гриф/подразделение (опора фильтра доступа, ТБ-020).
            (await db.Chunks.AllAsync(c => c.Classification == 1 && c.DivisionId == 7)).ShouldBeTrue();
        }

        // 2) Повтор того же содержимого — идемпотентность (ТНД-002): дублей нет.
        var second = await port.IngestAsync(request);
        second.ChunkCount.ShouldBe(0);
        await using (var db = factory.CreateDbContext())
        {
            (await db.Documents.CountAsync()).ShouldBe(1);
        }

        // 3) Документ без грифа — отказ, в индекс не попадает (fail-closed, ТБ-024).
        var rejected = await port.IngestAsync(request with { Title = "Без грифа", Text = "иной текст", Classification = null });
        rejected.Accepted.ShouldBeFalse();
        await using (var db = factory.CreateDbContext())
        {
            (await db.Documents.CountAsync()).ShouldBe(1); // новых документов не появилось
        }
    }

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
