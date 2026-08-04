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
/// скрывает диалог из списка и истории. Требуется Docker.
/// </summary>
public sealed class ConversationStoreTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("pgvector/pgvector:pg16").Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "Диалоги: история по порядку, гриф=max, разграничение по владельцу, мягкое удаление")]
    public async Task Conversation_lifecycle_and_owner_isolation()
    {
        var factory = new TestContextFactory(_postgres.GetConnectionString());
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

    // Контекст с теми же опциями, что в проде (snake_case + pgvector-маппинг).
    private sealed class TestContextFactory(string connectionString) : IDbContextFactory<CoreDbContext>
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
}
