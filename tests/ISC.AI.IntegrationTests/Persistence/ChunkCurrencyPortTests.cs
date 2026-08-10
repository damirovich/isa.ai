using ISC.AI.Persistence;
using ISC.AI.Persistence.Corpus;
using ISC.AI.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using Shouldly;
using Testcontainers.PostgreSql;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Порт годности (Э4-02, ADR-0013): согласованно гасит/включает <c>is_current</c> у чанка и его
/// эмбеддинга — опора фильтра актуальности retrieval. Реальный PostgreSQL+pgvector через Testcontainers.
/// </summary>
public sealed class ChunkCurrencyPortTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = TestPostgres.Create();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "Порт годности: гасит is_current у чанка и его эмбеддинга")]
    public async Task Sets_currency_on_chunk_and_embedding()
    {
        var factory = new CoreContextFactory(_postgres.GetConnectionString());

        int chunkId;
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();

            var document = new DocumentEntity { DocType = "приказ", Title = "Т", Classification = 0, DivisionId = 7 };
            db.Documents.Add(document);
            await db.SaveChangesAsync();

            var chunk = new ChunkEntity { DocumentId = document.Id, Ordinal = 0, Text = "t", Classification = 0, DivisionId = 7, IsCurrent = true };
            db.Chunks.Add(chunk);
            await db.SaveChangesAsync();
            chunkId = chunk.Id;

            var values = new float[EmbeddingEntity.Dimensions];
            values[0] = 1f;
            db.Embeddings.Add(new EmbeddingEntity { ChunkId = chunk.Id, Embedding = new Vector(values), ModelKey = "test", Classification = 0, DivisionId = 7, IsCurrent = true });
            await db.SaveChangesAsync();
        }

        var affected = await new ChunkCurrencyPort(factory).SetCurrencyAsync([chunkId], isCurrent: false);

        affected.ShouldBe(1);
        await using var verify = factory.CreateDbContext();
        (await verify.Chunks.FirstAsync(c => c.Id == chunkId)).IsCurrent.ShouldBeFalse();
        (await verify.Embeddings.FirstAsync(e => e.ChunkId == chunkId)).IsCurrent.ShouldBeFalse();
    }
}
