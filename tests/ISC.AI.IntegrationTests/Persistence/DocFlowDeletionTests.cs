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
/// Удаления справочных объектов и вложений (мелкие пробелы переноса СКИД). Требуется Docker.
/// </summary>
/// <remarks>
/// Ключевое здесь — что удалить МОЖНО ТОЛЬКО неиспользуемое. Удали тип, по которому есть документы,
/// и у них пропадёт группа, а с ней и правила поведения (§3.1): назначения, статусы, сроки.
/// </remarks>
public sealed class DocFlowDeletionTests : IAsyncLifetime
{
    private static readonly AccessContext Access = new("42", 9, [5]);

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("pgvector/pgvector:pg16").Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "Тип документа удаляется, только пока по нему нет документов")]
    public async Task Type_is_deletable_only_while_unused()
    {
        var (store, types) = await BuildAsync();

        var unused = (await types.CreateAsync("Неиспользуемый", DocumentGroup.Storage, isActive: true))!.Value;
        var used = (await types.CreateAsync("Поручение", DocumentGroup.Execution, isActive: true))!.Value;

        // Признак в списке считается вместе с выдачей — экран не должен предлагать невыполнимое.
        var before = await types.ListAsync();
        before.Single(t => t.Id == unused).CanDelete.ShouldBeTrue();
        before.Single(t => t.Id == used).CanDelete.ShouldBeTrue();

        await CreateDocumentAsync(store, used, "П-1");

        var after = await types.ListAsync();
        after.Single(t => t.Id == used).CanDelete.ShouldBeFalse();

        (await types.DeleteAsync(used)).ShouldBe(DocumentTypeWriteResult.HasDocuments);
        (await types.DeleteAsync(unused)).ShouldBe(DocumentTypeWriteResult.Ok);
        (await types.DeleteAsync(unused)).ShouldBe(DocumentTypeWriteResult.NotFound);

        // Использованный тип остался на месте — документ не осиротел.
        (await types.ListAsync()).ShouldHaveSingleItem().Id.ShouldBe(used);
    }

    [Fact(DisplayName = "Вложение удаляется вместе с файлом; чужой документ неотличим от несуществующего")]
    public async Task Attachment_is_deleted_with_its_file()
    {
        var storage = new RecordingStorage();
        var (store, types) = await BuildAsync(storage);
        var typeId = (await types.CreateAsync("Поручение", DocumentGroup.Execution, isActive: true))!.Value;
        var documentId = await CreateDocumentAsync(store, typeId, "П-1");

        (await store.AddAttachmentAsync(
            documentId, new UploadedFile("акт.pdf", "application/pdf", [1, 2, 3]), Access))
            .ShouldBe(DocumentWriteStatus.Ok);

        var attachment = (await store.GetAsync(documentId, Access))!.Attachments.ShouldHaveSingleItem();

        // Субъект без допуска к документу не должен даже узнать, что вложение существует.
        var stranger = new AccessContext("60", 0, [9]);
        (await store.DeleteAttachmentAsync(attachment.Id, stranger)).ShouldBe(DocumentWriteStatus.NotFound);
        (await store.GetAsync(documentId, Access))!.Attachments.ShouldHaveSingleItem();

        (await store.DeleteAttachmentAsync(attachment.Id, Access)).ShouldBe(DocumentWriteStatus.Ok);

        (await store.GetAsync(documentId, Access))!.Attachments.ShouldBeEmpty();
        storage.Deleted.ShouldContain(attachment.StoredFileName);

        // Повтор — «нет такого», а не ошибка: строка уже снята.
        (await store.DeleteAttachmentAsync(attachment.Id, Access)).ShouldBe(DocumentWriteStatus.NotFound);
    }

    private static async Task<int> CreateDocumentAsync(DocumentStore store, int typeId, string regNumber)
    {
        var result = await store.CreateAsync(
            new DocumentDraft(regNumber, new DateOnly(2026, 2, 1), typeId, DocumentDirection.Incoming,
                null, $"Содержание {regNumber}", null, null, DocumentPriority.Medium, 7, 0, 5, 42),
            [new AssignmentDraft(5, 11, new DateOnly(2026, 5, 1))],
            useCommonDeadline: false, commonDeadline: null, Access);

        result.Status.ShouldBe(DocumentWriteStatus.Ok);
        return result.DocumentId;
    }

    private async Task<(DocumentStore Store, DocumentTypeStore Types)> BuildAsync(
        IDocFlowFileStorage? storage = null)
    {
        var factory = new DocFlowContextFactory(_postgres.GetConnectionString());
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        return (
            new DocumentStore(
                factory, storage ?? new RecordingStorage(), new AllowAllAccessPolicy(),
                TestUserDirectory.AllowAll),
            new DocumentTypeStore(factory));
    }

    /// <summary>Хранилище-протокол: запоминает, что просили удалить, — без обращения к диску.</summary>
    private sealed class RecordingStorage : IDocFlowFileStorage
    {
        public List<string> Deleted { get; } = [];

        public Task<string> SaveAsync(
            Stream content, string extension, string category, string subPath,
            CancellationToken cancellationToken = default) =>
            Task.FromResult($"{Guid.NewGuid():N}{extension}");

        public Task<Stream> OpenReadAsync(
            string storedFileName, string category, string subPath, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream>(new MemoryStream());

        public Task DeleteAsync(
            string storedFileName, string category, string subPath, CancellationToken cancellationToken = default)
        {
            Deleted.Add(storedFileName);
            return Task.CompletedTask;
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
}
