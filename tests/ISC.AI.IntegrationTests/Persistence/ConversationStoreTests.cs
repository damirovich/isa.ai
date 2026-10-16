using ISC.AI.Abstractions.Conversations;
using ISC.AI.Abstractions.Enums;
using ISC.AI.Persistence;
using ISC.AI.Persistence.Conversations;
using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;
using Shouldly;
using Testcontainers.PostgreSql;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Хранилище диалогов на реальном PostgreSQL: история сохраняется по порядку, гриф диалога = максимум
/// грифов сообщений, разграничение по владельцу (чужой субъект не видит/не меняет), мягкое удаление
/// скрывает диалог из списка и истории; области профиля, сокрытие сообщений выше допуска, источники и ключи
/// уничтожения, физическое уничтожение по ключу (ADR-0052). Требуется Docker.
/// </summary>
public sealed class ConversationStoreTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = TestPostgres.Create();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "Диалоги: история по порядку, гриф=max, разграничение по владельцу, мягкое удаление")]
    public async Task Conversation_lifecycle_and_owner_isolation()
    {
        var factory = new CoreContextFactory(_postgres.GetConnectionString());
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        var store = new ConversationStore(factory);
        const int owner = 42;
        const int other = 99;

        var conversationId = await store.CreateAsync(owner, "Про приказы");
        (await store.AppendMessageAsync(conversationId, owner, ConversationMessageRole.User, "вопрос 1", 0, null)).ShouldBeTrue();
        (await store.AppendMessageAsync(conversationId, owner, ConversationMessageRole.Assistant, "ответ 1", 2, "{}")).ShouldBeTrue();

        // История — обе реплики по порядку.
        var history = await store.GetHistoryAsync(conversationId, owner);
        history.Count.ShouldBe(2);
        history[0].Role.ShouldBe(ConversationMessageRole.User);
        history[0].Text.ShouldBe("вопрос 1");
        history[1].Role.ShouldBe(ConversationMessageRole.Assistant);
        history[1].Text.ShouldBe("ответ 1");

        // Список владельца: диалог есть, гриф поднялся до максимума грифов сообщений (2).
        var list = await store.ListAsync(owner);
        list.Count.ShouldBe(1);
        list[0].Classification.ShouldBe<short>(2);

        // Разграничение: чужой субъект не видит и не может писать/переименовывать/удалять.
        (await store.GetHistoryAsync(conversationId, other)).ShouldBeEmpty();
        (await store.AppendMessageAsync(conversationId, other, ConversationMessageRole.User, "чужое", 0, null)).ShouldBeFalse();
        (await store.RenameAsync(conversationId, other, "взлом")).ShouldBeFalse();
        (await store.DeleteAsync(conversationId, other)).ShouldBeFalse();
        (await store.ListAsync(other)).ShouldBeEmpty();

        // Переименование владельцем.
        (await store.RenameAsync(conversationId, owner, "Новое имя")).ShouldBeTrue();
        (await store.ListAsync(owner)).ShouldHaveSingleItem().Title.ShouldBe("Новое имя");

        // Мягкое удаление владельцем — скрывает из списка и истории.
        (await store.DeleteAsync(conversationId, owner)).ShouldBeTrue();
        (await store.ListAsync(owner)).ShouldBeEmpty();
        (await store.GetHistoryAsync(conversationId, owner)).ShouldBeEmpty();
    }

    [Fact(DisplayName = "ADR-0052: диалоги по областям; прежний диалог — в области по умолчанию; источники и проверка хранятся; выше допуска — скрыто")]
    public async Task Areas_messages_and_clearance()
    {
        var factory = new CoreContextFactory(_postgres.GetConnectionString());
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        var store = new ConversationStore(factory);
        const int owner = 7;

        // Диалог, заведённый до областей (прежний путь ядра), — в области по умолчанию: виден там, где и был.
        var legacy = await store.CreateAsync(owner, "прежний");
        var inCase = await store.CreateAsync(owner, "по делу", " case:45 ", 2, ["case:45", "case:45", " "]);
        (await store.ListInAreaAsync(owner, null)).Select(c => c.Id).ShouldBe([legacy]);
        (await store.ListInAreaAsync(owner, "case:45")).ShouldHaveSingleItem().Id.ShouldBe(inCase);
        (await store.ListInAreaAsync(owner, "case:46")).ShouldBeEmpty();
        (await store.ListInAreaAsync(99, "case:45")).ShouldBeEmpty();

        var summary = (await store.FindAsync(inCase, owner)).ShouldNotBeNull();
        summary.Area.ShouldBe("case:45");
        summary.Classification.ShouldBe<short>(2);
        summary.UpdatedAt.Kind.ShouldBe(DateTimeKind.Utc);
        (await store.FindAsync(inCase, 99)).ShouldBeNull();

        (await store.AppendMessageAsync(inCase, owner, new ConversationMessageDraft(ConversationMessageRole.User, "вопрос", 2))).ShouldBeTrue();
        (await store.AppendMessageAsync(inCase, owner, new ConversationMessageDraft(
            ConversationMessageRole.Assistant, "ответ [Фрагмент 1]", 3, "{\"citations\":[]}", "[{\"number\":1}]", "{\"unverified\":[]}",
            ["case:45", "case:47"]))).ShouldBeTrue();
        (await store.AppendMessageAsync(inCase, 99, new ConversationMessageDraft(ConversationMessageRole.User, "чужое", 0))).ShouldBeFalse();

        var thread = (await store.GetAsync(inCase, owner, maxClassification: 3)).ShouldNotBeNull();
        thread.Area.ShouldBe("case:45");
        thread.Classification.ShouldBe<short>(3);
        thread.HiddenMessages.ShouldBe(0);
        thread.Messages.Select(m => m.Role).ShouldBe([ConversationMessageRole.User, ConversationMessageRole.Assistant]);
        thread.Messages[1].SourcesJson.ShouldNotBeNull().ShouldContain("number");
        thread.Messages[1].CheckJson.ShouldNotBeNull().ShouldContain("unverified");
        thread.Messages[1].CreatedAt.Kind.ShouldBe(DateTimeKind.Utc);
        thread.Messages[0].SourcesJson.ShouldBeNull();

        // Допуск понизили: ответ с грифом 3 не выдаётся, только их число (ТБ-020); чужому — ничего.
        var lowered = (await store.GetAsync(inCase, owner, maxClassification: 2)).ShouldNotBeNull();
        lowered.Messages.ShouldHaveSingleItem().Role.ShouldBe(ConversationMessageRole.User);
        lowered.HiddenMessages.ShouldBe(1);
        (await store.GetAsync(inCase, 99, maxClassification: 4)).ShouldBeNull();

        // Ключи: область + материалы ответа, без повторов и пустых.
        await using (var db = factory.CreateDbContext())
        {
            (await db.Conversations.AsNoTracking().SingleAsync(c => c.Id == inCase)).ScopeKeys.ShouldBe(["case:45", "case:47"]);
            (await db.Conversations.AsNoTracking().SingleAsync(c => c.Id == legacy)).ScopeKeys.ShouldBeEmpty();
        }
    }

    [Fact(DisplayName = "ADR-0052: уничтожение по ключу — физически, у любых владельцев, со скрытыми и с сообщениями; чужие ключи не задеты")]
    public async Task Purge_by_scope_key()
    {
        var factory = new CoreContextFactory(_postgres.GetConnectionString());
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        var store = new ConversationStore(factory);
        var byCase = await store.CreateAsync(1, "по делу 45", "case:45", 2, ["case:45"]);
        var byAll = await store.CreateAsync(2, "по всем делам", null, 0, []);
        await store.AppendMessageAsync(byAll, 2, new ConversationMessageDraft(ConversationMessageRole.Assistant, "ответ", 2, ScopeKeys: ["case:45"]));
        var hidden = await store.CreateAsync(1, "скрытый", "case:45", 2, ["case:45"]);
        await store.AppendMessageAsync(hidden, 1, new ConversationMessageDraft(ConversationMessageRole.User, "вопрос", 2));
        (await store.DeleteAsync(hidden, 1)).ShouldBeTrue();
        var other = await store.CreateAsync(1, "по делу 46", "case:46", 2, ["case:46"]);
        var prefix = await store.CreateAsync(1, "по делу 450", "case:450", 2, ["case:450"]);

        (await store.CountScopeAsync("case:45")).ShouldBe(3);
        (await store.PurgeScopeAsync("case:45")).ShouldBe(3);
        (await store.CountScopeAsync("case:45")).ShouldBe(0);

        await using (var db = factory.CreateDbContext())
        {
            (await db.Conversations.IgnoreQueryFilters().Select(c => c.Id).OrderBy(id => id).ToListAsync()).ShouldBe([other, prefix]);
            (await db.ConversationMessages.IgnoreQueryFilters().AnyAsync(m => m.ConversationId == byAll || m.ConversationId == hidden))
                .ShouldBeFalse();
            (await db.ConversationMessages.IgnoreQueryFilters().AnyAsync(m => m.ConversationId == byCase)).ShouldBeFalse();
        }

        await Should.ThrowAsync<ArgumentException>(() => store.PurgeScopeAsync(" "));
    }
}
