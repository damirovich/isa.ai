using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.AI.Retrieval;
using ISC.AI.Abstractions.BackgroundTasks;
using ISC.AI.Abstractions.Enums;
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
/// Чат по документам дела (ADR-0051) на настоящей БД: область извлечения — заданные документы корпуса (точный перебор,
/// ближе по смыслу, но вне области — не выдаётся; пустая область — пусто; floor допуска не ослабляется); область дела —
/// видимые документы дела, уже проиндексированные; диалоги — свои, по видимым делам, с сокрытием сообщений выше
/// допуска; уничтожение дела уносит переписку каскадом.
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

        (await retriever.RetrieveAsync("вопрос", access, topK: 5))[0].DocumentId.ShouldBe(nearest);
        var scoped = await retriever.RetrieveAsync("вопрос", access, topK: 5, new RetrievalFilter(DocumentIds: [inScope, secret]));
        scoped.ShouldHaveSingleItem().DocumentId.ShouldBe(inScope);
        (await retriever.RetrieveAsync("вопрос", access, topK: 5, new RetrievalFilter(DocumentIds: []))).ShouldBeEmpty();
    }

    [Fact(DisplayName = "ADR-0051: область дела — видимые документы дела с текстом в корпусе; диалоги свои, по видимым делам; выше допуска — скрыто; уничтожение дела уносит переписку")]
    public async Task Case_scope_and_chat_store()
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

        // 10 — Администратор (видит всё и регистрирует документы), 11 — Руководитель (видит дело, но диалог не его),
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

        // Диалоги: свой по видимому делу; руководитель дело видит, но чужого диалога — нет; следователь без дела — ничего.
        var chats = new CaseChatStore(investigation, policy, roles, TimeProvider.System);
        (await chats.GetScopeAsync(caseId, admin)).ShouldBe(new CaseChatScope(caseId, "ЧАТ-1", 2, 5, "Дело ЧАТ-1"));
        (await chats.ListScopesAsync(admin)).Select(s => s.CaseId).ShouldBe([caseId]);
        (await chats.ListScopesAsync(stranger)).ShouldBeEmpty();
        (await chats.GetScopeAsync(caseId, stranger)).ShouldBeNull();
        var chatId = (await chats.CreateAsync(caseId, "  Что   изъяли?  ", admin)).ShouldNotBeNull();
        (await chats.CreateAsync(caseId, "x", stranger)).ShouldBeNull();
        (await chats.ListAsync(caseId, admin)).ShouldNotBeNull().ShouldHaveSingleItem().Title.ShouldBe("Что изъяли?");
        (await chats.ListAsync(caseId, head)).ShouldNotBeNull().ShouldBeEmpty();
        (await chats.ListAsync(caseId, stranger)).ShouldBeNull();

        (await chats.AppendAsync(chatId, new CaseChatMessageDraft(ConversationMessageRole.User, "Что изъяли?", 2), admin)).ShouldBeTrue();
        var source = new CaseChatSource(1, ChatSourceKind.Document, indexed, caseId, "№ 1-с от 10.01.2024 · Справка", 77, 3, "Изъяты телефон…");
        (await chats.AppendAsync(chatId, new CaseChatMessageDraft(
            ConversationMessageRole.Assistant, "Телефон [Фрагмент 1].", 3, [source], new CaseChatCheck(["№ 9"], [4])), admin)).ShouldBeTrue();
        (await chats.AppendAsync(chatId, new CaseChatMessageDraft(ConversationMessageRole.User, "чужой", 2), head)).ShouldBeFalse();

        var thread = (await chats.GetAsync(chatId, admin)).ShouldNotBeNull();
        thread.Classification.ShouldBe<short>(3);
        thread.Messages.Count.ShouldBe(2);
        thread.Messages[1].Sources.ShouldHaveSingleItem().ShouldBe(source);
        thread.Messages[1].Check.ShouldNotBeNull().Unverified.ShouldBe(["№ 9"]);
        thread.Messages[1].Check!.UnknownFragments.ShouldBe([4]);
        thread.Messages[1].CreatedAt.Kind.ShouldBe(DateTimeKind.Utc);

        // Допуск понизили до грифа дела: ответ с грифом 3 скрыт, виден только счётчик.
        var lowered = InvestigationTestKit.Access(10, 2, 5);
        var hidden = (await chats.GetAsync(chatId, lowered)).ShouldNotBeNull();
        hidden.Messages.ShouldHaveSingleItem().Role.ShouldBe(ConversationMessageRole.User);
        hidden.HiddenMessages.ShouldBe(1);

        (await chats.GetAsync(chatId, head)).ShouldBeNull();
        (await chats.RenameAsync(chatId, "Изъятие", head)).ShouldBeFalse();
        (await chats.DeleteAsync(chatId, head)).ShouldBeFalse();
        (await chats.RenameAsync(chatId, "Изъятие", admin)).ShouldBeTrue();
        (await chats.ListAsync(caseId, admin))!.Single().Title.ShouldBe("Изъятие");

        // Диалоги по всем делам: свой видит только владелец; тот, где ответ опирался на это дело, уйдёт вместе с делом,
        // другой — останется.
        var global = (await chats.CreateAsync(null, "По всем", admin)).ShouldNotBeNull();
        (await chats.AppendAsync(global, new CaseChatMessageDraft(ConversationMessageRole.Assistant, "Ответ [Фрагмент 1].", 2, [source]), admin)).ShouldBeTrue();
        var unrelated = (await chats.CreateAsync(null, "Другое", admin)).ShouldNotBeNull();
        (await chats.AppendAsync(unrelated, new CaseChatMessageDraft(ConversationMessageRole.Assistant, "Ответ.", 0,
            [source with { CaseId = caseId + 1000 }]), admin)).ShouldBeTrue();
        (await chats.ListAsync(null, admin)).ShouldNotBeNull().Count.ShouldBe(2);
        (await chats.ListAsync(null, head)).ShouldNotBeNull().ShouldBeEmpty();
        (await chats.GetAsync(global, admin)).ShouldNotBeNull().CaseId.ShouldBeNull();

        // Уничтожение дела: переписка по делу и диалог по всем делам, опиравшийся на него, — в составе и уходят.
        (await cases.GetCompositionAsync(caseId, admin)).ShouldNotBeNull().Chats.ShouldBe(2);
        (await cases.PurgeAsync(caseId, admin)).ShouldBe(CaseWriteResult.Ok);
        await using (var db = investigation.CreateDbContext())
        {
            (await db.CaseChats.Select(c => c.Id).ToListAsync()).ShouldBe([unrelated]);
            (await db.CaseChatMessages.Select(m => m.ChatId).ToListAsync()).ShouldBe([unrelated]);
        }
    }

    [Fact(DisplayName = "ADR-0051: удаление своего диалога — физически, вместе с сообщениями")]
    public async Task Delete_removes_chat_and_messages()
    {
        var connection = _postgres.GetConnectionString();
        var investigation = new InvestigationContextFactory(connection);
        var core = new CoreContextFactory(connection);
        await InvestigationTestKit.MigrateAsync(investigation);
        await using (var db = core.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        await InvestigationTestKit.AssignRolesAsync(investigation, (20, InvestigationRole.Investigator));
        var owner = InvestigationTestKit.Access(20, 3, 6);
        var cases = InvestigationTestKit.CreateCaseStore(investigation, core);
        var caseId = (await cases.CreateAsync(InvestigationTestKit.Draft("ЧАТ-2", 6, 1, 20), owner)).CaseId;
        var chats = new CaseChatStore(investigation, new InvestigationAccessPolicy(investigation), new UserRoleStore(core, investigation), TimeProvider.System);
        var chatId = (await chats.CreateAsync(caseId, "", owner)).ShouldNotBeNull();
        (await chats.ListAsync(caseId, owner))!.Single().Title.ShouldBe("Новый диалог");
        await chats.AppendAsync(chatId, new CaseChatMessageDraft(ConversationMessageRole.User, "вопрос", 1), owner);

        (await chats.DeleteAsync(chatId, owner)).ShouldBeTrue();

        await using var check = investigation.CreateDbContext();
        (await check.CaseChats.AnyAsync(c => c.Id == chatId)).ShouldBeFalse();
        (await check.CaseChatMessages.AnyAsync(m => m.ChatId == chatId)).ShouldBeFalse();
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
