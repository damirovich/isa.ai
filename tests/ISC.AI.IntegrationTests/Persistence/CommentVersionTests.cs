using ISC.AI.Abstractions.Security;
using ISC.AI.AI.Security;
using ISC.AI.Modules.DocFlow.Data;
using ISC.AI.Modules.DocFlow.Domain.Enums;
using ISC.AI.Modules.DocFlow.Domain.Services;
using ISC.AI.Persistence;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Pgvector.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Привязка комментария к версии файла и скрытие решённых обсуждений (§4.8). Требуется Docker.
/// </summary>
/// <remarks>
/// Смысл привязки: замечание «в пункте 3 ошибка» относится к ТОМУ тексту, который автор читал.
/// Файл документа версионируется (§3.3), и после замены комментарий без отметки версии начинал бы
/// указывать не туда — читатель ищет пункт 3 в новой редакции и не находит либо находит другой.
/// </remarks>
public sealed class CommentVersionTests : IAsyncLifetime
{
    private static readonly AccessContext Access = new("42", 9, [5]);

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("pgvector/pgvector:pg16").Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "Комментарий запоминает версию файла и помечается, когда та устарела")]
    public async Task Comment_remembers_the_file_version()
    {
        var (documents, comments, types) = await BuildAsync();
        var typeId = (await types.CreateAsync("Поручение", DocumentGroup.Execution, isActive: true))!.Value;
        var documentId = await CreateDocumentAsync(documents, typeId);

        // Комментарий ДО появления файла: обсуждали сам документ, версии нет.
        await AddAsync(comments, documentId, "Общий вопрос");

        await UploadAsync(documents, documentId, "проект.txt");
        await AddAsync(comments, documentId, "Замечание к первой редакции");

        // Замена файла создаёт вторую версию (§3.3).
        await UploadAsync(documents, documentId, "проект.txt");
        await AddAsync(comments, documentId, "Замечание ко второй редакции");

        var feed = await comments.ListAsync(documentId);
        feed.Count.ShouldBe(3);

        feed[0].DocumentFileVersion.ShouldBeNull();
        feed[0].IsAboutCurrentVersion.ShouldBeTrue();

        // Первая версия уже не актуальна — комментарий помечен как относящийся к прежней редакции.
        feed[1].DocumentFileVersion.ShouldBe(1);
        feed[1].IsAboutCurrentVersion.ShouldBeFalse();

        feed[2].DocumentFileVersion.ShouldBe(2);
        feed[2].IsAboutCurrentVersion.ShouldBeTrue();
    }

    [Fact(DisplayName = "Скрытие решённых убирает ветку целиком, вместе с ответами")]
    public async Task Hiding_resolved_removes_the_whole_thread()
    {
        var (documents, comments, types) = await BuildAsync();
        var typeId = (await types.CreateAsync("Поручение", DocumentGroup.Execution, isActive: true))!.Value;
        var documentId = await CreateDocumentAsync(documents, typeId);

        var rootId = await AddAsync(comments, documentId, "Вопрос");
        await AddAsync(comments, documentId, "Ответ", parentId: rootId);
        await AddAsync(comments, documentId, "Открытое обсуждение");

        (await comments.ListAsync(documentId)).Count.ShouldBe(3);

        (await comments.SetResolvedAsync(rootId, resolved: true, actorUserId: 42))
            .ShouldBe(CommentWriteStatus.Ok);

        // Ответ уходит вместе с корнем: реплика без вопроса читается как обрывок.
        var open = await comments.ListAsync(documentId, includeResolved: false);
        open.ShouldHaveSingleItem().Content.ShouldBe("Открытое обсуждение");

        // Без фильтра лента прежняя — ничего не потеряно.
        (await comments.ListAsync(documentId)).Count.ShouldBe(3);
    }

    private static async Task<int> AddAsync(
        CommentStore comments, int documentId, string content, int? parentId = null)
    {
        var (status, id) = await comments.AddAsync(
            new CommentDraft(documentId, parentId, content, CommentType.Question, [], null), 42);

        status.ShouldBe(CommentWriteStatus.Ok);
        return id;
    }

    private static async Task UploadAsync(DocumentStore documents, int documentId, string fileName)
    {
        var status = await documents.AddDocumentFileAsync(
            documentId, new UploadedFile(fileName, "text/plain", [1, 2, 3]),
            DocumentLanguage.Russian, Access);

        status.ShouldBe(DocumentWriteStatus.Ok);
    }

    private static async Task<int> CreateDocumentAsync(DocumentStore store, int typeId)
    {
        var result = await store.CreateAsync(
            new DocumentDraft("П-1", new DateOnly(2026, 2, 1), typeId, DocumentDirection.Incoming,
                null, "Содержание", null, null, DocumentPriority.Medium, 7, 0, 5, 42),
            [new AssignmentDraft(5, 11, new DateOnly(2026, 5, 1))],
            useCommonDeadline: false, commonDeadline: null, Access);

        result.Status.ShouldBe(DocumentWriteStatus.Ok);
        return result.DocumentId;
    }

    private async Task<(DocumentStore Documents, CommentStore Comments, DocumentTypeStore Types)> BuildAsync()
    {
        var factory = new DocFlowContextFactory(_postgres.GetConnectionString());
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        var core = new CoreContextFactory(_postgres.GetConnectionString());
        await using (var db = core.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        var storage = new MemoryStorage();
        return (
            new DocumentStore(factory, storage, new AllowAllAccessPolicy(), TestUserDirectory.AllowAll),
            new CommentStore(factory, core, storage),
            new DocumentTypeStore(factory));
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

    /// <summary>Хранилище в памяти: тесту важны версии в БД, а не байты на диске.</summary>
    private sealed class MemoryStorage : IDocFlowFileStorage
    {
        public Task<string> SaveAsync(
            Stream content, string extension, string category, string subPath,
            CancellationToken cancellationToken = default) =>
            Task.FromResult($"{Guid.NewGuid():N}{extension}");

        public Task<Stream> OpenReadAsync(
            string storedFileName, string category, string subPath, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream>(new MemoryStream());

        public Task DeleteAsync(
            string storedFileName, string category, string subPath, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
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
