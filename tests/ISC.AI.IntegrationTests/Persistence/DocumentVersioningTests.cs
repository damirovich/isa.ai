using ISC.AI.Abstractions.Ingestion;
using ISC.AI.Ingestion;
using ISC.AI.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Shouldly;
using Testcontainers.PostgreSql;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Версионирование документов при загрузке (Э4-14) на настоящем PostgreSQL+pgvector: загрузка новой
/// версии с указанием заменяемого документа ГАСИТ прежнюю (её чанки и эмбеддинги становятся неактуальными),
/// в актуальных остаётся только новая версия — опора фильтра актуальности retrieval (GATE-3): в ИИ/поиск
/// уходит лишь последняя версия. Прежний документ помечается заменённым; замена несуществующего — отказ.
/// </summary>
/// <remarks>Требуется Docker. Эмбеддер — фиксированный фейк (проверяется инвариант, не качество).</remarks>
[Trait("Category", "Gate")]
public sealed class DocumentVersioningTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("pgvector/pgvector:pg16").Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "Новая версия гасит прежнюю: старые чанки неактуальны, актуальна только новая (GATE-3)")]
    public async Task Ingesting_new_version_supersedes_previous()
    {
        var factory = new CoreContextFactory(_postgres.GetConnectionString());
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        var port = new IngestionPort(factory, new FixedEmbeddingGenerator(768), new SimpleTextChunker());

        // Версия 1.
        var v1 = await port.IngestAsync(new IngestionRequest(
            "положение", "Положение о контроле", "Первая редакция. Пункт один. Пункт два.",
            Classification: 1, DivisionId: 7));
        v1.Accepted.ShouldBeTrue();
        v1.DocumentId.ShouldNotBeNull();
        v1.ChunkCount.ShouldBeGreaterThan(0);
        var v1Id = v1.DocumentId!.Value;

        // Версия 2 — заменяет версию 1.
        var v2 = await port.IngestAsync(new IngestionRequest(
            "положение", "Положение о контроле (ред. 2)", "Вторая редакция. Иной пункт один. Иной пункт два.",
            Classification: 1, DivisionId: 7, SupersedesDocumentId: v1Id));
        v2.Accepted.ShouldBeTrue();
        v2.DocumentId.ShouldNotBeNull();
        var v2Id = v2.DocumentId!.Value;
        v2.SupersededDocumentId.ShouldBe(v1Id);
        v2.SupersededChunkCount.ShouldBe(v1.ChunkCount);

        await using (var db = factory.CreateDbContext())
        {
            var v1ChunkIds = await db.Chunks.Where(c => c.DocumentId == v1Id).Select(c => c.Id).ToListAsync();

            // Прежняя версия: все чанки и их эмбеддинги погашены.
            (await db.Chunks.Where(c => c.DocumentId == v1Id).AllAsync(c => !c.IsCurrent)).ShouldBeTrue();
            (await db.Embeddings.Where(e => v1ChunkIds.Contains(e.ChunkId)).AllAsync(e => !e.IsCurrent)).ShouldBeTrue();

            // Новая версия: все чанки актуальны; в актуальных — ТОЛЬКО новая версия.
            (await db.Chunks.Where(c => c.DocumentId == v2Id).AllAsync(c => c.IsCurrent)).ShouldBeTrue();
            (await db.Chunks.Where(c => c.IsCurrent).AllAsync(c => c.DocumentId == v2Id)).ShouldBeTrue();

            // Прежний документ помечен заменённым (история версий).
            var v1Doc = await db.Documents.SingleAsync(d => d.Id == v1Id);
            v1Doc.SupersededByDocumentId.ShouldBe(v2Id);
        }
    }

    [Fact(DisplayName = "Замена несуществующего документа — отказ, новый документ не создаётся")]
    public async Task Superseding_missing_document_is_rejected()
    {
        var factory = new CoreContextFactory(_postgres.GetConnectionString());
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        var port = new IngestionPort(factory, new FixedEmbeddingGenerator(768), new SimpleTextChunker());

        var result = await port.IngestAsync(new IngestionRequest(
            "положение", "Сирота", "Замена несуществующего документа.", Classification: 0, DivisionId: 1,
            SupersedesDocumentId: 999_999));

        result.Accepted.ShouldBeFalse();
        await using (var db = factory.CreateDbContext())
        {
            (await db.Documents.CountAsync()).ShouldBe(0);
        }
    }
}
