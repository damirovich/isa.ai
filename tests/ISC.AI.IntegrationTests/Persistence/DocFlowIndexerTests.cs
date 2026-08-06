using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.AI.Security;
using ISC.AI.Ingestion;
using ISC.AI.Modules.DocFlow.Data;
using ISC.AI.Modules.DocFlow.Domain.Enums;
using ISC.AI.Modules.DocFlow.Domain.Services;
using ISC.AI.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using NSubstitute;
using Pgvector.EntityFrameworkCore;
using Shouldly;
using Testcontainers.PostgreSql;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Индексация документов docflow в корпус ядра на настоящем PostgreSQL+pgvector (Э4-35 этап 7):
/// текст с грифом/подразделением уходит в core.document/chunk, мостик ведётся, повтор дедуплицируется,
/// переиндексация гасит прежнюю версию (supersede). Эмбеддер — фиксированный фейк. Требуется Docker.
/// </summary>
public sealed class DocFlowIndexerTests : IAsyncLifetime
{
    // Допуск автора: проверяется индексация, не разграничение (оно — в InspectorAccessPolicyTests).
    private static readonly AccessContext FullAccess = new("42", 10, [10]);

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("pgvector/pgvector:pg16").Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "Индексация: гриф/подразделение доезжают до чанков, мостик ведётся, переиндексация гасит прежнюю версию")]
    public async Task Document_indexing_lifecycle()
    {
        var connectionString = _postgres.GetConnectionString();
        var docFlowFactory = new DocFlowContextFactory(connectionString);
        var coreFactory = new CoreContextFactory(connectionString);
        await using (var db = docFlowFactory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        await using (var db = coreFactory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        var typeStore = new DocumentTypeStore(docFlowFactory);
        var documentStore = new DocumentStore(
            docFlowFactory, Substitute.For<IDocFlowFileStorage>(), new AllowAllAccessPolicy(), TestUserDirectory.AllowAll);
        var port = new IngestionPort(coreFactory, new FixedEmbeddingGenerator(768), new SimpleTextChunker());
        var indexer = new DocFlowDocumentIndexer(
            docFlowFactory, coreFactory, port, Substitute.For<IAuditWriter>());

        var typeId = await typeStore.CreateAsync("Поручение", DocumentGroup.Execution, isActive: true);
        var created = await documentStore.CreateAsync(
            new DocumentDraft("П-7", new DateOnly(2026, 8, 1), typeId!.Value, DocumentDirection.Incoming,
                null, "Проверить склад вооружения", "Полный текст поручения о проверке склада.",
                null, DocumentPriority.High, 77, 2, 10, 42),
            [new AssignmentDraft(10, null, new DateOnly(2026, 9, 1))],
            useCommonDeadline: true, commonDeadline: new DateOnly(2026, 9, 1), FullAccess);
        created.Status.ShouldBe(DocumentWriteStatus.Ok);

        // 1) Индексация: чанки с грифом/подразделением ДОКУМЕНТА, мостик создан, метаданные — обратная ссылка.
        var first = await indexer.IndexAsync(created.DocumentId);
        first.Status.ShouldBe(DocumentIndexStatus.Indexed);
        first.ChunkCount.ShouldBeGreaterThan(0);
        var coreId = first.CoreDocumentId!.Value;
        await using (var core = coreFactory.CreateDbContext())
        {
            (await core.Chunks.Where(c => c.DocumentId == coreId)
                .AllAsync(c => c.Classification == 2 && c.DivisionId == 10 && c.IsCurrent)).ShouldBeTrue();
            var coreDoc = await core.Documents.SingleAsync(d => d.Id == coreId);
            coreDoc.Metadata!["docflow_document_id"].ShouldBe(created.DocumentId.ToString());
            coreDoc.Title.ShouldStartWith("П-7");
        }

        await using (var db = docFlowFactory.CreateDbContext())
        {
            (await db.DocumentIndexLinks.SingleAsync(l => l.DocumentId == created.DocumentId))
                .CoreDocumentId.ShouldBe(coreId);
        }

        // 2) Повтор без изменений — дедуп ядра (ТНД-002): корпус не растёт.
        (await indexer.IndexAsync(created.DocumentId)).Status.ShouldBe(DocumentIndexStatus.Unchanged);
        await using (var core = coreFactory.CreateDbContext())
        {
            (await core.Documents.CountAsync()).ShouldBe(1);
        }

        // 3) Текст изменился — переиндексация: новый корпусный документ, прежний погашен (GATE-3).
        await using (var db = docFlowFactory.CreateDbContext())
        {
            var document = await db.Documents.SingleAsync(d => d.Id == created.DocumentId);
            document.FullText = "Дополненный текст поручения: срок продлён, объём расширен.";
            await db.SaveChangesAsync();
        }

        var second = await indexer.IndexAsync(created.DocumentId);
        second.Status.ShouldBe(DocumentIndexStatus.Indexed);
        var newCoreId = second.CoreDocumentId!.Value;
        newCoreId.ShouldNotBe(coreId);
        await using (var core = coreFactory.CreateDbContext())
        {
            (await core.Documents.SingleAsync(d => d.Id == coreId))
                .SupersededByDocumentId.ShouldBe(newCoreId);
            (await core.Chunks.Where(c => c.DocumentId == coreId).AllAsync(c => !c.IsCurrent)).ShouldBeTrue();
            (await core.Chunks.Where(c => c.DocumentId == newCoreId).AllAsync(c => c.IsCurrent)).ShouldBeTrue();
        }

        await using (var db = docFlowFactory.CreateDbContext())
        {
            var link = await db.DocumentIndexLinks.SingleAsync(l => l.DocumentId == created.DocumentId);
            link.CoreDocumentId.ShouldBe(newCoreId);
        }
    }

    private sealed class DocFlowContextFactory(string connectionString) : IDbContextFactory<DocFlowDbContext>
    {
        public DocFlowDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<DocFlowDbContext>()
                .UseNpgsql(connectionString, npg =>
                    npg.MigrationsHistoryTable("__ef_migrations_history", DocFlowDbContext.Schema))
                .UseSnakeCaseNamingConvention()
                .Options);
    }

    private sealed class CoreContextFactory(string connectionString) : IDbContextFactory<CoreDbContext>
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

    // Фиксированный эмбеддер (как в IngestionPipelineTests): проверяется конвейер, не качество векторов.
    private sealed class FixedEmbeddingGenerator(int dimensions) : IEmbeddingGenerator<string, Embedding<float>>
    {
        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var vector = new float[dimensions];
            vector[0] = 1f;
            var list = values.Select(_ => new Embedding<float>(vector)).ToList();
            return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(list));
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
