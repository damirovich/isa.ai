using ISC.AI.Abstractions.Security;
using ISC.AI.AI.Security;
using ISC.AI.Modules.DocFlow.Data;
using ISC.AI.Modules.DocFlow.Domain.Enums;
using ISC.AI.Modules.DocFlow.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Testcontainers.PostgreSql;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Резолвинг файла по маршруту раздачи (Э4-35 этап 4.3): каждая из 4 категорий отдаёт гриф/подразделение
/// ВЛАДЕЮЩЕГО документа, а несовпадение <c>parentId</c> с фактическим родителем — не отличается наружу
/// от «файла нет» (защита от подмены маршрута). Требуется Docker.
/// </summary>
public sealed class DocumentFileAccessResolverTests : IAsyncLifetime
{
    // Допуск автора документа: тесту нужен сам резолвинг, решётка здесь не предмет проверки.
    private static readonly AccessContext FullAccess = new("42", 10, [20]);

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("pgvector/pgvector:pg16").Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "Резолвер: 4 категории отдают гриф/подразделение документа; подмена parentId — null")]
    public async Task Resolve_returns_owning_document_access_and_rejects_wrong_parent()
    {
        var factory = new TestContextFactory(_postgres.GetConnectionString());
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        var storage = new TempFileStorage();
        var typeStore = new DocumentTypeStore(factory);
        var documentStore = new DocumentStore(factory, storage, new AllowAllAccessPolicy(), TestUserDirectory.AllowAll);
        var resolver = new DocumentFileAccessResolver(factory);

        var typeId = await typeStore.CreateAsync("Поручение", DocumentGroup.Execution, isActive: true);
        var doc = await documentStore.CreateAsync(
            new DocumentDraft(null, new DateOnly(2026, 8, 5), typeId!.Value, DocumentDirection.Incoming,
                null, "Документ с файлами всех категорий", null, null, DocumentPriority.Medium, 77, 5, 20, 42),
            [new AssignmentDraft(20, null, new DateOnly(2026, 9, 1))],
            useCommonDeadline: false, commonDeadline: null, FullAccess);
        doc.Status.ShouldBe(DocumentWriteStatus.Ok);

        var assignmentId = (await documentStore.GetAsync(doc.DocumentId, FullAccess))!.Assignments.Single().Id;

        // Документ + вложение.
        (await documentStore.AddDocumentFileAsync(doc.DocumentId,
            new UploadedFile("файл.docx", "application/msword", [1, 2, 3]), DocumentLanguage.Russian, FullAccess))
            .ShouldBe(DocumentWriteStatus.Ok);
        (await documentStore.AddAttachmentAsync(doc.DocumentId,
            new UploadedFile("прил.pdf", "application/pdf", [4, 5]), FullAccess))
            .ShouldBe(DocumentWriteStatus.Ok);

        // Файл к переходу статуса + файл к продлению (§4.2/§4.6).
        (await documentStore.ChangeAssignmentStatusAsync(assignmentId, AssignmentStatus.InProgress, "старт", FullAccess,
            [new UploadedFile("акт.pdf", "application/pdf", [6, 7])]))
            .ShouldBe(DocumentWriteStatus.Ok);
        (await documentStore.ExtendDeadlineAsync(assignmentId, new DateOnly(2026, 10, 1), "продление", FullAccess,
            [new UploadedFile("обоснование.pdf", "application/pdf", [8, 9])]))
            .ShouldBe(DocumentWriteStatus.Ok);

        var details = await documentStore.GetAsync(doc.DocumentId, FullAccess);
        var documentFileName = details!.Files.Single().StoredFileName;
        var attachmentFileName = details.Attachments.Single().StoredFileName;

        string historyFileName, extensionFileName;
        await using (var db = factory.CreateDbContext())
        {
            historyFileName = (await db.StatusHistoryFiles.SingleAsync()).StoredFileName;
            extensionFileName = (await db.DeadlineExtensionFiles.SingleAsync()).StoredFileName;
        }

        // Правильный parentId — резолвится с грифом/подразделением ДОКУМЕНТА (5 / 20) во всех 4 категориях.
        foreach (var (category, parentId, storedFileName) in new[]
        {
            (FileCategories.Documents, doc.DocumentId, documentFileName),
            (FileCategories.Attachments, doc.DocumentId, attachmentFileName),
            (FileCategories.StatusHistory, assignmentId, historyFileName),
            (FileCategories.DeadlineExtensions, assignmentId, extensionFileName),
        })
        {
            var resolved = await resolver.ResolveAsync(category, parentId, storedFileName);
            resolved.ShouldNotBeNull(customMessage: $"категория «{category}» обязана резолвиться");
            resolved.Classification.ShouldBe<short>(5);
            resolved.DivisionId.ShouldBe(20);
        }

        // Подмена parentId (чужой документ/назначение в маршруте) — файл не отдаётся.
        (await resolver.ResolveAsync(FileCategories.Documents, 999_999, documentFileName)).ShouldBeNull();
        (await resolver.ResolveAsync(FileCategories.StatusHistory, 999_999, historyFileName)).ShouldBeNull();

        // Неизвестная категория / несуществующее имя — тоже null, не исключение.
        (await resolver.ResolveAsync("unknown", doc.DocumentId, documentFileName)).ShouldBeNull();
        (await resolver.ResolveAsync(FileCategories.Documents, doc.DocumentId, "нет-такого.docx")).ShouldBeNull();
    }

    private sealed class TempFileStorage : IDocFlowFileStorage
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(), "iscai-docflow-tests", Guid.NewGuid().ToString("N"));

        public async Task<string> SaveAsync(
            Stream content, string extension, string category, string subPath,
            CancellationToken cancellationToken = default)
        {
            var storedFileName = Guid.NewGuid().ToString("N") + extension;
            var directory = Path.Combine(_root, category, subPath);
            Directory.CreateDirectory(directory);
            await using var fileStream = File.Create(Path.Combine(directory, storedFileName));
            await content.CopyToAsync(fileStream, cancellationToken);
            return storedFileName;
        }

        public Task<Stream> OpenReadAsync(
            string storedFileName, string category, string subPath, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream>(File.OpenRead(Path.Combine(_root, category, subPath, storedFileName)));

        public Task DeleteAsync(
            string storedFileName, string category, string subPath, CancellationToken cancellationToken = default)
        {
            var path = Path.Combine(_root, category, subPath, storedFileName);
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class TestContextFactory(string connectionString) : IDbContextFactory<DocFlowDbContext>
    {
        public DocFlowDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<DocFlowDbContext>()
                .UseNpgsql(connectionString, npg =>
                    npg.MigrationsHistoryTable("__ef_migrations_history", DocFlowDbContext.Schema))
                .UseSnakeCaseNamingConvention()
                .Options);
    }
}
