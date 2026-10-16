using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.AI.Retrieval;
using ISC.AI.Abstractions.BackgroundTasks;
using ISC.AI.Abstractions.Retrieval;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Data;
using ISC.AI.Modules.DocFlow.Domain.Entities;
using ISC.AI.Modules.DocFlow.Domain.Enums;
using ISC.AI.Modules.DocFlow.Domain.Services;
using ISC.AI.Modules.Media.Domain.Services;
using ISC.AI.Persistence;
using ISC.AI.Persistence.Entities;
using ISC.AI.Profile.Investigation.Application.Features.CaseChat;
using ISC.AI.Profile.Investigation.Application.Features.CaseDocuments;
using ISC.AI.Profile.Investigation.Data;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Pgvector;
using Shouldly;
using Testcontainers.PostgreSql;
using Xunit;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Чат по материалам дел (ADR-0051/0052) на настоящей БД: область извлечения — заданные документы корпуса (точный
/// перебор, ближе по смыслу, но вне области — не выдаётся; пустая область — пусто; floor допуска не ослабляется;
/// фрагмент несёт заголовок документа); область дела — видимые документы дела, уже проиндексированные; дела-области
/// чата — только видимые по решётке. Переписку ведёт пакет «Чат» в таблицах ядра — её проверяют
/// <c>ConversationStoreTests</c> и <c>CasePurgeTests</c>.
/// </summary>
/// <remarks>Требуется Docker. Схемы <c>core</c>, <c>docflow</c>, <c>investigation</c> — в одном контейнере.</remarks>
[Trait("Category", "Gate")]
public sealed class CaseChatScopeTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = TestPostgres.Create();
    private readonly string _files = Path.Combine(Path.GetTempPath(), "iscai-case-chat-tests", Guid.NewGuid().ToString("N"));

    public Task InitializeAsync() => _postgres.StartAsync();

    public async Task DisposeAsync()
    {
        await _postgres.DisposeAsync();
        if (Directory.Exists(_files))
        {
            Directory.Delete(_files, recursive: true);
        }
    }

    [Fact(DisplayName = "ADR-0051: область — только заданные документы (ближайший вне области не выдаётся), пустая область — пусто, гриф выше допуска — не выдаётся")]
    public async Task Retriever_scope_narrows_and_keeps_floor()
    {
        var core = new CoreContextFactory(_postgres.GetConnectionString());
        int inScope, nearest, secret;
        await using (var db = core.CreateDbContext())
        {
            await db.Database.MigrateAsync();
            inScope = await AddDocumentAsync(db, classification: 1, axis: 1);  // дальше от запроса
            nearest = await AddDocumentAsync(db, classification: 1, axis: 0);  // ближе всех, но вне области
            secret = await AddDocumentAsync(db, classification: 3, axis: 0);   // в области, но выше допуска
        }

        var retriever = new PgVectorRetriever(core, new FixedEmbeddingGenerator(EmbeddingEntity.Dimensions), new AllowAllAccessPolicy(), RetrievalOptions.None);
        var access = new AccessContext("u1", MaxClassification: 1, AllowedDivisions: [7]);

        var all = await retriever.RetrieveAsync("вопрос", access, topK: 5);
        all[0].DocumentId.ShouldBe(nearest);

        // Заголовок документа — нейтральная подпись источника для любого профиля (ADR-0052).
        all[0].DocumentTitle.ShouldBe("Т");
        var scoped = await retriever.RetrieveAsync("вопрос", access, topK: 5, new RetrievalFilter(DocumentIds: [inScope, secret]));
        scoped.ShouldHaveSingleItem().DocumentId.ShouldBe(inScope);
        (await retriever.RetrieveAsync("вопрос", access, topK: 5, new RetrievalFilter(DocumentIds: []))).ShouldBeEmpty();
    }

    [Fact(DisplayName = "ADR-0051/0052: область дела — видимые документы дела с текстом в корпусе; дела-области чата — только видимые по решётке")]
    public async Task Case_scope_and_chat_areas()
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

        // 10 — Администратор (видит всё и регистрирует документы), 11 — Руководитель (видит дело по решётке),
        // 12 — Следователь без этого дела.
        await InvestigationTestKit.AssignRolesAsync(investigation,
            (10, InvestigationRole.Administrator), (11, InvestigationRole.Head), (12, InvestigationRole.Investigator));
        var admin = InvestigationTestKit.Access(10, 3, 5);
        var head = InvestigationTestKit.Access(11, 3, 5);
        var stranger = InvestigationTestKit.Access(12, 3, 5);
        var cases = InvestigationTestKit.CreateCaseStore(investigation, core);
        var caseId = (await cases.CreateAsync(InvestigationTestKit.Draft("ЧАТ-1", 5, 2, 10), admin)).CaseId;

        // Два документа дела через приём архива (ADR-0042): тот же путь, что у живых документов дела.
        var types = new DocumentTypeStore(docflow);
        var type = (await types.CreateAsync("Справка", DocumentGroup.Storage, isActive: true)).ShouldNotBeNull();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DocFlow:Storage:BasePath"] = _files })
            .Build();
        var documents = new DocumentStore(docflow, new LocalDocFlowFileStorage(configuration), new AllowAllAccessPolicy(), TestUserDirectory.AllowAll);
        var policy = new InvestigationAccessPolicy(investigation);
        var roles = new UserRoleStore(core, investigation);
        var links = new CaseDocumentStore(investigation, policy, roles);
        var sink = new ArchiveDocumentSink(documents, types, links, new NoQueue());
        var caseItem = new CaseScopeItem(caseId, "ЧАТ-1", "Дело", 2, 5);
        var indexed = await ImportAsync(sink, caseItem, type, "1-с", admin);
        await ImportAsync(sink, caseItem, type, "2-с", admin);

        // Проиндексирован только первый: мостик в корпус есть лишь у него.
        await using (var db = docflow.CreateDbContext())
        {
            db.DocumentIndexLinks.Add(new DocumentIndexLink { DocumentId = indexed, CoreDocumentId = 9001, IndexedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        // Документооборот отдаёт корпусные версии только видимых и проиндексированных документов дела.
        var lookup = new DocumentLookup(docflow, policy);
        var documentIds = (await links.ListLinksAsync([caseId], admin)).Select(l => l.DocumentId).ToList();
        documentIds.Count.ShouldBe(2);
        (await lookup.ResolveByIdsAsync(documentIds, admin)).Count.ShouldBe(2);
        (await lookup.ResolveCorpusAsync(documentIds, admin)).ShouldHaveSingleItem()
            .ShouldBe(new DocumentCorpusRef(indexed, 9001, "1-с", new DateOnly(2024, 1, 10), "Справка"));
        (await links.ListLinksAsync([caseId], stranger)).ShouldBeEmpty();

        // Дела-области чата (ADR-0052): видимое дело — с номером, грифом, подразделением и названием; следователь без
        // этого дела не видит ни его, ни области «все мои дела» с ним; руководитель видит дело по решётке.
        var scopes = new CaseChatScopes(investigation, policy, roles);
        (await scopes.GetScopeAsync(caseId, admin)).ShouldBe(new CaseChatScope(caseId, "ЧАТ-1", 2, 5, "Дело ЧАТ-1"));
        (await scopes.ListScopesAsync(admin)).Select(s => s.CaseId).ShouldBe([caseId]);
        (await scopes.GetScopeAsync(caseId, head)).ShouldNotBeNull();
        (await scopes.ListScopesAsync(stranger)).ShouldBeEmpty();
        (await scopes.GetScopeAsync(caseId, stranger)).ShouldBeNull();
        (await scopes.GetScopeAsync(caseId + 1000, admin)).ShouldBeNull();

        // Без права «Дашборд и реестр дел» (субъект без роли) — ни одного дела.
        var noRole = InvestigationTestKit.Access(13, 3, 5);
        (await scopes.ListScopesAsync(noRole)).ShouldBeEmpty();
    }

    // ---------- обвязка ----------

    private static async Task<int> ImportAsync(ArchiveDocumentSink sink, CaseScopeItem caseItem, int type, string number, AccessContext access)
    {
        var result = await sink.ImportAsync(
            new ArchiveDocumentRequest(caseItem, number + ".txt", "text/plain", [0x31], type, number, new DateOnly(2024, 1, 10),
                "Справка " + number, null, "тест", ExistingDocumentId: null),
            access,
            _ => Task.CompletedTask);
        return result.DocumentId.ShouldNotBeNull();
    }

    /// <summary>Документ корпуса с одним фрагментом: вектор по оси <paramref name="axis"/> (запрос фейка — ось 0).</summary>
    private static async Task<int> AddDocumentAsync(CoreDbContext db, short classification, int axis)
    {
        var document = new DocumentEntity { DocType = "справка", Title = "Т", Classification = classification, DivisionId = 7 };
        db.Documents.Add(document);
        await db.SaveChangesAsync();
        var chunk = new ChunkEntity
        {
            DocumentId = document.Id, Ordinal = 0, Text = "фрагмент", Classification = classification, DivisionId = 7, IsCurrent = true,
        };
        db.Chunks.Add(chunk);
        await db.SaveChangesAsync();
        var values = new float[EmbeddingEntity.Dimensions];
        values[axis] = 1f;
        db.Embeddings.Add(new EmbeddingEntity
        {
            ChunkId = chunk.Id, Embedding = new Vector(values), ModelKey = "test", Classification = classification, DivisionId = 7, IsCurrent = true,
        });
        await db.SaveChangesAsync();
        return document.Id;
    }

    private sealed class NoQueue : IBackgroundTaskQueue
    {
        public ValueTask<Guid> EnqueueAsync(
            string kind, Func<IServiceProvider, CancellationToken, Task> work, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Guid.NewGuid());
    }
}
