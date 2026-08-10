using ISC.AI.Modules.DocFlow.Data;
using ISC.AI.Modules.DocFlow.Domain.Entities;
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
        var factory = new DocFlowContextFactory(_postgres.GetConnectionString());
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

    [Fact(DisplayName = "Смена группы запрещена при наличии документов типа (ТЗ СКИД §3.1)")]
    public async Task Group_change_rejected_when_documents_exist()
    {
        var factory = new DocFlowContextFactory(_postgres.GetConnectionString());
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        var store = new DocumentTypeStore(factory);
        var typeId = await store.CreateAsync("Поручение", DocumentGroup.Execution, isActive: true);
        typeId.ShouldNotBeNull();

        // Документ этого типа (регистрация «в лоб» через контекст — сценарии регистрации появятся на этапе 3).
        await using (var db = factory.CreateDbContext())
        {
            db.Documents.Add(new Document
            {
                TypeId = typeId.Value,
                RegDate = new DateOnly(2026, 8, 5),
                DirectionFlag = DocumentDirection.Incoming,
                ShortContent = "Тестовое поручение",
                Classification = 0,
                DivisionId = 10,
            });
            await db.SaveChangesAsync();
        }

        // Инвариант §3.1: у типа есть документы — смена группы отклоняется, группа не изменилась.
        (await store.ChangeGroupAsync(typeId.Value, DocumentGroup.Storage))
            .ShouldBe(DocumentTypeWriteResult.HasDocuments);
        (await store.ListAsync(group: DocumentGroup.Execution)).ShouldHaveSingleItem();
    }
}
