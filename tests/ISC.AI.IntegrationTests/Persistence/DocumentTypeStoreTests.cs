using ISC.AI.Modules.DocFlow.Data;
using ISC.AI.Modules.DocFlow.Domain.Enums;
using ISC.AI.Modules.DocFlow.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Testcontainers.PostgreSql;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Хранилище справочника типов документов на реальном PostgreSQL (Э4-35 этап 1, ТЗ СКИД §3.1):
/// создание с уникальностью имени, фильтры списка, правка/деактивация, смена группы. Требуется Docker.
/// </summary>
public sealed class DocumentTypeStoreTests : IAsyncLifetime
{
    // Тот же образ, что и в остальных тестах решения (уже закеширован), pgvector не используется.
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("pgvector/pgvector:pg16").Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "Типы документов: уникальность имени, фильтры, правка, смена группы")]
    public async Task Document_type_dictionary_lifecycle()
    {
        var factory = new TestContextFactory(_postgres.GetConnectionString());
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        var store = new DocumentTypeStore(factory);

        // Создание: два типа разных групп.
        var instruction = await store.CreateAsync("Поручение", DocumentGroup.Execution, isActive: true);
        instruction.ShouldNotBeNull();
        var reference = await store.CreateAsync("Справка", DocumentGroup.Storage, isActive: true);
        reference.ShouldNotBeNull();

        // Уникальность имени: дубликат отклоняется дружелюбно (null), не исключением.
        (await store.CreateAsync("Поручение", DocumentGroup.Storage, isActive: true)).ShouldBeNull();

        // Список: сортировка по имени, фильтры по группе и активности.
        var all = await store.ListAsync();
        all.Count.ShouldBe(2);
        all[0].Name.ShouldBe("Поручение");
        (await store.ListAsync(group: DocumentGroup.Execution)).ShouldHaveSingleItem().Name.ShouldBe("Поручение");

        // Правка: переименование + деактивация; занятое имя — отказ; несуществующий — NotFound.
        (await store.UpdateAsync(reference.Value, "Справка архивная", isActive: false))
            .ShouldBe(DocumentTypeWriteResult.Ok);
        (await store.ListAsync(isActive: true)).ShouldHaveSingleItem().Name.ShouldBe("Поручение");
        (await store.UpdateAsync(instruction.Value, "Справка архивная", isActive: true))
            .ShouldBe(DocumentTypeWriteResult.NameTaken);
        (await store.UpdateAsync(9999, "Нет такого", isActive: true))
            .ShouldBe(DocumentTypeWriteResult.NotFound);

        // Смена группы: документов в системе нет (этап 2 впереди) — разрешена; несуществующий — NotFound.
        (await store.ChangeGroupAsync(reference.Value, DocumentGroup.Execution))
            .ShouldBe(DocumentTypeWriteResult.Ok);
        (await store.ChangeGroupAsync(9999, DocumentGroup.Storage))
            .ShouldBe(DocumentTypeWriteResult.NotFound);
        (await store.ListAsync(group: DocumentGroup.Execution)).Count.ShouldBe(2);
    }

    // Контекст с теми же опциями, что в проде (snake_case + история миграций в схеме docflow).
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
