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
        var factory = new CoreContextFactory(_postgres.GetConnectionString());
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

    [Fact(DisplayName = "Retriever: метаданные документа доезжают до RetrievedChunk.Metadata (этап 7.2 Э4-35 — ссылки-источники в чате)")]
    public async Task Retriever_projects_document_metadata_onto_chunk()
    {
        var factory = new CoreContextFactory(_postgres.GetConnectionString());
        DocumentEntity doc;
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();

            doc = new DocumentEntity
            {
                DocType = "поручение",
                Title = "П-1 · Тест",
                Classification = 0,
                DivisionId = 7,
                Metadata = new Dictionary<string, string> { ["docflow_document_id"] = "555", ["reg_number"] = "П-1" },
            };
            db.Documents.Add(doc);
            await db.SaveChangesAsync();
            await AddChunkWithEmbeddingAsync(db, doc.Id, ordinal: 0, classification: 0, divisionId: 7, isCurrent: true);
        }

        var retriever = new PgVectorRetriever(
            factory, new FixedEmbeddingGenerator(EmbeddingEntity.Dimensions), new AllowAllAccessPolicy(), RetrievalOptions.None);
        var access = new AccessContext("u1", MaxClassification: 5, AllowedDivisions: [7]);

        var chunk = (await retriever.RetrieveAsync("любой запрос", access, topK: 10)).ShouldHaveSingleItem();
        chunk.Metadata.ShouldNotBeNull();
        chunk.Metadata!["docflow_document_id"].ShouldBe("555");
        chunk.Metadata["reg_number"].ShouldBe("П-1");
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
}
