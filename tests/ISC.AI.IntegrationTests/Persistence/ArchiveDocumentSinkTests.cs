using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.BackgroundTasks;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Data;
using ISC.AI.Modules.DocFlow.Domain.Enums;
using ISC.AI.Modules.Media.Domain.Services;
using ISC.AI.Profile.Investigation.Application.Features.CaseDocuments;
using ISC.AI.Profile.Investigation.Data;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Testcontainers.PostgreSql;
using Xunit;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Документы архива в «Следствии» (ADR-0042) на настоящей БД: документ документооборота группы «Хранение» с грифом и
/// подразделением дела, файл, привязка к делу; продолжение прерванной попытки без второго документа; только типы
/// «Хранение»; индексация — задачей документооборота.
/// </summary>
[Trait("Category", "Gate")]
public sealed class ArchiveDocumentSinkTests : IAsyncLifetime
{
    private const int Operator = 10;

    private readonly PostgreSqlContainer _postgres = TestPostgres.Create();
    private readonly string _files = Path.Combine(Path.GetTempPath(), "iscai-sink-tests", Guid.NewGuid().ToString("N"));

    public Task InitializeAsync() => _postgres.StartAsync();

    public async Task DisposeAsync()
    {
        await _postgres.DisposeAsync();
        if (Directory.Exists(_files))
        {
            Directory.Delete(_files, recursive: true);
        }
    }

    [Fact(DisplayName = "ADR-0042: документ архива — «Хранение», гриф и подразделение дела, файл и привязка к делу; повтор продолжает тот же документ; типы только «Хранение»")]
    public async Task Sink_registers_document_in_case()
    {
        var connection = _postgres.GetConnectionString();
        var investigation = new InvestigationContextFactory(connection);
        var core = new CoreContextFactory(connection);
        var docflow = new DocFlowContextFactory(connection);
        await InvestigationTestKit.MigrateAsync(investigation);
        await using (var db = core.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        await using (var db = docflow.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        await InvestigationTestKit.AssignRolesAsync(investigation, (Operator, InvestigationRole.Administrator));
        var access = InvestigationTestKit.Access(Operator, 3, 5);
        var cases = InvestigationTestKit.CreateCaseStore(investigation, core);
        var caseId = (await cases.CreateAsync(InvestigationTestKit.Draft("АРХ-1", 5, 2, Operator), access)).CaseId;

        var types = new DocumentTypeStore(docflow);
        var storageType = (await types.CreateAsync("Архивная сводка", DocumentGroup.Storage, isActive: true)).ShouldNotBeNull();
        await types.CreateAsync("Поручение", DocumentGroup.Execution, isActive: true);
        await types.CreateAsync("Старый тип", DocumentGroup.Storage, isActive: false);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DocFlow:Storage:BasePath"] = _files })
            .Build();
        var documents = new DocumentStore(docflow, new LocalDocFlowFileStorage(configuration), new AllowAllAccessPolicy(), TestUserDirectory.AllowAll);
        var links = new CaseDocumentStore(investigation, new InvestigationAccessPolicy(investigation), new UserRoleStore(core, investigation));
        var queue = new RecordingQueue();
        var sink = new ArchiveDocumentSink(documents, types, links, queue);

        (await sink.ListTypesAsync()).Select(t => t.Name).ShouldBe(["Архивная сводка"]);

        var caseItem = new CaseScopeItem(caseId, "АРХ-1", "Дело", 2, 5);
        var content = new byte[] { 0x50, 0x4B, 0x03, 0x04, 1, 2, 3 };
        var created = new List<int>();
        var result = await sink.ImportAsync(
            new ArchiveDocumentRequest(caseItem, "сводка.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                content, storageType, "12-с", new DateOnly(2023, 5, 14), "Сводка за 14 мая", "архив ОН",
                "Импорт архива: задание 1, строка описи 2", ExistingDocumentId: null),
            access,
            id =>
            {
                created.Add(id);
                return Task.CompletedTask;
            });

        var documentId = result.DocumentId.ShouldNotBeNull();
        created.ShouldBe([documentId]);

        var details = (await documents.GetAsync(documentId, access)).ShouldNotBeNull();
        details.Classification.ShouldBe<short>(2);
        details.DivisionId.ShouldBe(5);
        details.RegNumber.ShouldBe("12-с");
        details.RegDate.ShouldBe(new DateOnly(2023, 5, 14));
        details.ShortContent.ShouldBe("Сводка за 14 мая");
        details.Files.ShouldHaveSingleItem().FileName.ShouldBe("сводка.docx");
        (await links.ListAsync(caseId, access)).ShouldNotBeNull().ShouldHaveSingleItem().DocumentId.ShouldBe(documentId);

        // Повтор прерванной попытки: тот же документ, без второго файла и второй привязки, onCreated не вызывается.
        var resumed = await sink.ImportAsync(
            new ArchiveDocumentRequest(caseItem, "сводка.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                content, storageType, "12-с", new DateOnly(2023, 5, 14), "Сводка за 14 мая", null, "повтор", documentId),
            access,
            _ => throw new InvalidOperationException("второй документ создаваться не должен"));
        resumed.DocumentId.ShouldBe(documentId);
        (await documents.GetAsync(documentId, access))!.Files.Count.ShouldBe(1);
        await using (var db = docflow.CreateDbContext())
        {
            (await db.Documents.CountAsync()).ShouldBe(1);
        }

        // Тот же рег. номер вторым документом — понятный отказ, а не исключение.
        var taken = await sink.ImportAsync(
            new ArchiveDocumentRequest(caseItem, "другой.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                content, storageType, "12-с", new DateOnly(2023, 5, 15), "Другая", null, "x", null),
            access, _ => Task.CompletedTask);
        taken.DocumentId.ShouldBeNull();
        taken.Error.ShouldBe("документ с таким рег. номером уже зарегистрирован");

        // Индексация — задачей документооборота в общей очереди.
        await sink.EnqueueIndexingAsync(documentId);
        queue.Kinds.ShouldBe([ArchiveDocumentSink.IndexingTaskKind]);
    }

    private sealed class RecordingQueue : IBackgroundTaskQueue
    {
        public List<string> Kinds { get; } = [];

        public ValueTask<Guid> EnqueueAsync(
            string kind, Func<IServiceProvider, CancellationToken, Task> work, CancellationToken cancellationToken = default)
        {
            Kinds.Add(kind);
            return ValueTask.FromResult(Guid.NewGuid());
        }
    }
}
