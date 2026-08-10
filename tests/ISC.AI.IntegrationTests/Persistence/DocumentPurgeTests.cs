using System.Globalization;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Corpus;
using ISC.AI.Abstractions.Enums;
using ISC.AI.Persistence;
using ISC.AI.Persistence.Audit;
using ISC.AI.Persistence.Corpus;
using ISC.AI.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using Shouldly;
using Testcontainers.PostgreSql;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// ТБ-064: гарантированное удаление документа и ВСЕХ его производных (чанки, эмбеддинги в pgvector,
/// задания индексации) — физически, с записью в аудит и снятием «висячих» ссылок-преемников. Проверка
/// на реальном PostgreSQL+pgvector через Testcontainers (каскад БД <c>ON DELETE CASCADE</c> — настоящий).
/// </summary>
/// <remarks>Требуется Docker.</remarks>
[Trait("Category", "Gate")]
public sealed class DocumentPurgeTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("pgvector/pgvector:pg16").Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "ТБ-064: документ и все производные удалены физически, факт записан в аудит")]
    public async Task Purge_removes_document_and_all_derivatives_and_audits()
    {
        var factory = new CoreContextFactory(_postgres.GetConnectionString());
        int targetId, supersededDocId;
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
            (targetId, supersededDocId) = await SeedAsync(db);
        }

        var purger = new DocumentPurger(factory, new AuditWriter(factory));

        var result = await purger.PurgeAsync(targetId, subjectId: 42);

        result.Found.ShouldBeTrue();
        result.StorageUri.ShouldBe("file://vault/target.docx");

        await using (var db = factory.CreateDbContext())
        {
            // Документ и все его производные физически отсутствуют.
            (await db.Documents.AnyAsync(d => d.Id == targetId)).ShouldBeFalse();
            (await db.Chunks.AnyAsync(c => c.DocumentId == targetId)).ShouldBeFalse();
            (await db.Embeddings.CountAsync()).ShouldBe(0); // все эмбеддинги принадлежали удалённым чанкам
            (await db.IndexingJobs.AnyAsync(j => j.DocumentId == targetId)).ShouldBeFalse();

            // Другой документ уцелел, но его «висячая» ссылка-преемник на удалённый — снята.
            var superseded = await db.Documents.SingleAsync(d => d.Id == supersededDocId);
            superseded.SupersededByDocumentId.ShouldBeNull();

            // Факт удаления зафиксирован в неизменяемом аудите (действие Purge, субъект и объект указаны).
            var audit = await db.AuditRecords.SingleAsync(r => r.Action == AuditAction.Purge);
            audit.ObjectRef.ShouldBe(targetId.ToString(CultureInfo.InvariantCulture));
            audit.SubjectId.ShouldBe(42);
            audit.Classification.ShouldBe<short>(1);
        }
    }

    [Fact(DisplayName = "ТБ-064: удаление несуществующего документа идемпотентно и не пишет аудит")]
    public async Task Purge_of_missing_document_is_idempotent_without_audit()
    {
        var factory = new CoreContextFactory(_postgres.GetConnectionString());
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        var purger = new DocumentPurger(factory, new AuditWriter(factory));

        var result = await purger.PurgeAsync(documentId: 999_999, subjectId: 7);

        result.Found.ShouldBeFalse();
        await using (var db = factory.CreateDbContext())
        {
            (await db.AuditRecords.AnyAsync()).ShouldBeFalse(); // удалять было нечего — журнал пуст
        }
    }

    // Возвращает (id удаляемого документа, id документа со ссылкой-преемником на него).
    private static async Task<(int TargetId, int SupersededDocId)> SeedAsync(CoreDbContext db)
    {
        var target = new DocumentEntity
        {
            DocType = "приказ",
            Title = "Удаляемый",
            Classification = 1,
            DivisionId = 7,
            StorageUri = "file://vault/target.docx",
        };
        db.Documents.Add(target);
        await db.SaveChangesAsync();

        await AddChunkWithEmbeddingAsync(db, target.Id, ordinal: 0);
        await AddChunkWithEmbeddingAsync(db, target.Id, ordinal: 1);

        db.IndexingJobs.Add(new IndexingJobEntity { DocumentId = target.Id, Status = IndexingJobStatus.Completed });
        await db.SaveChangesAsync();

        // Другой документ, помеченный как заменённый удаляемым (слабая ссылка-преемник, Э4-14).
        var superseded = new DocumentEntity
        {
            DocType = "приказ",
            Title = "Заменённый удаляемым",
            Classification = 1,
            DivisionId = 7,
            SupersededByDocumentId = target.Id,
        };
        db.Documents.Add(superseded);
        await db.SaveChangesAsync();

        return (target.Id, superseded.Id);
    }

    private static async Task AddChunkWithEmbeddingAsync(CoreDbContext db, int documentId, int ordinal)
    {
        var chunk = new ChunkEntity
        {
            DocumentId = documentId,
            Ordinal = ordinal,
            Text = $"фрагмент {ordinal}",
            Classification = 1,
            DivisionId = 7,
        };
        db.Chunks.Add(chunk);
        await db.SaveChangesAsync();

        var values = new float[EmbeddingEntity.Dimensions];
        values[0] = 1f;
        db.Embeddings.Add(new EmbeddingEntity
        {
            ChunkId = chunk.Id,
            Embedding = new Vector(values),
            ModelKey = "test",
            Classification = 1,
            DivisionId = 7,
        });
        await db.SaveChangesAsync();
    }
}
