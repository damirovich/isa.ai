using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Data;
using ISC.AI.Modules.DocFlow.Domain.Enums;
using ISC.AI.Modules.DocFlow.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Testcontainers.PostgreSql;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Правка реквизитов зарегистрированного документа (§3.2 ТЗ СКИД) на реальном PostgreSQL.
/// Требуется Docker.
/// </summary>
/// <remarks>
/// Проверяются прежде всего ТРИ ПРАВИЛА, которых в СКИД не было и которые нельзя проверить формой:
/// невозможность понизить гриф (рассекречивание), невозможность увести документ за пределы своего
/// допуска и невозможность сменить группу типа (в СКИД такая смена оставляла висячие назначения).
/// </remarks>
public sealed class DocumentUpdateTests : IAsyncLifetime
{
    private static readonly AccessContext Author = new("42", 5, [5, 9]);

    private readonly PostgreSqlContainer _postgres = TestPostgres.Create();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "Правка сохраняет реквизиты и перечисляет изменённые поля для журнала")]
    public async Task Update_saves_and_reports_changed_fields()
    {
        var (store, types) = await BuildAsync();
        var typeId = (await types.CreateAsync("Поручение", DocumentGroup.Execution, isActive: true))!.Value;
        var documentId = await CreateAsync(store, typeId, "П-1");

        var result = await store.UpdateAsync(
            Edit(documentId, typeId) with { ShortContent = "Новое содержание", Notes = "Уточнено" },
            Author);

        result.Status.ShouldBe(DocumentWriteStatus.Ok);
        result.Notice!.ChangedFields.ShouldBe(["краткое содержание", "примечания"], ignoreOrder: true);

        var saved = await store.GetAsync(documentId, Author);
        saved!.ShortContent.ShouldBe("Новое содержание");
        saved.Notes.ShouldBe("Уточнено");
    }

    /// <summary>
    /// ИНВАРИАНТ ТБ-020: понижение грифа — рассекречивание. Одним полем формы документ ДСП стал бы
    /// виден всем, у кого допуск ниже, задним числом и без отдельного следа.
    /// </summary>
    [Fact(DisplayName = "Гриф понизить правкой нельзя, повысить в пределах допуска — можно")]
    public async Task Classification_cannot_be_lowered_but_can_be_raised()
    {
        var (store, types) = await BuildAsync();
        var typeId = (await types.CreateAsync("Поручение", DocumentGroup.Execution, isActive: true))!.Value;
        var documentId = await CreateAsync(store, typeId, "П-1", classification: 2);

        var lowered = await store.UpdateAsync(
            Edit(documentId, typeId) with { Classification = 0 }, Author);
        lowered.Status.ShouldBe(DocumentWriteStatus.ClassificationDowngradeNotAllowed);

        // Гриф в базе не тронут — отказ произошёл ДО сохранения.
        (await store.GetAsync(documentId, Author))!.Classification.ShouldBe((short)2);

        var raised = await store.UpdateAsync(
            Edit(documentId, typeId) with { Classification = 4 }, Author);
        raised.Status.ShouldBe(DocumentWriteStatus.Ok);
        (await store.GetAsync(documentId, Author))!.Classification.ShouldBe((short)4);

        // Выше СВОЕГО допуска (5) поднять нельзя: документ исчез бы у того, кто его правит.
        var tooHigh = await store.UpdateAsync(
            Edit(documentId, typeId) with { Classification = 7 }, Author);
        tooHigh.Status.ShouldBe(DocumentWriteStatus.ClassificationOutsideClearance);
    }

    [Fact(DisplayName = "Подразделение можно сменить только на разрешённое субъекту")]
    public async Task Division_must_stay_within_clearance()
    {
        var (store, types) = await BuildAsync();
        var typeId = (await types.CreateAsync("Поручение", DocumentGroup.Execution, isActive: true))!.Value;
        var documentId = await CreateAsync(store, typeId, "П-1");

        var foreign = await store.UpdateAsync(Edit(documentId, typeId) with { DivisionId = 77 }, Author);
        foreign.Status.ShouldBe(DocumentWriteStatus.DivisionOutsideClearance);

        var allowed = await store.UpdateAsync(Edit(documentId, typeId) with { DivisionId = 9 }, Author);
        allowed.Status.ShouldBe(DocumentWriteStatus.Ok);
    }

    /// <summary>
    /// ИСПРАВЛЕНИЕ ДЕФЕКТА СКИД: там смена типа на «Хранение» обнуляла приоритет и инспектора,
    /// а назначения документа оставались в базе — висячие поручения у документа, который поручений
    /// иметь не может, но которые продолжали участвовать в проверке сроков.
    /// </summary>
    [Fact(DisplayName = "Сменить группу типа правкой нельзя — иначе назначения повисают")]
    public async Task Type_group_cannot_change()
    {
        var (store, types) = await BuildAsync();
        var execution = (await types.CreateAsync("Поручение", DocumentGroup.Execution, isActive: true))!.Value;
        var storage = (await types.CreateAsync("Справка", DocumentGroup.Storage, isActive: true))!.Value;
        var documentId = await CreateAsync(store, execution, "П-1");

        var result = await store.UpdateAsync(Edit(documentId, execution) with { TypeId = storage }, Author);

        result.Status.ShouldBe(DocumentWriteStatus.TypeGroupChangeNotAllowed);

        // Назначение на месте — документ не потерял исполнение.
        (await store.GetAsync(documentId, Author))!.Assignments.ShouldHaveSingleItem();
    }

    [Fact(DisplayName = "Рег. номер проверяется на занятость, но сам документ себе не мешает")]
    public async Task Reg_number_uniqueness_excludes_self()
    {
        var (store, types) = await BuildAsync();
        var typeId = (await types.CreateAsync("Поручение", DocumentGroup.Execution, isActive: true))!.Value;
        var first = await CreateAsync(store, typeId, "П-1");
        await CreateAsync(store, typeId, "П-2");

        // Сохранение БЕЗ смены номера не должно отбиваться «номер занят» им же самим.
        (await store.UpdateAsync(Edit(first, typeId) with { RegNumber = "П-1" }, Author))
            .Status.ShouldBe(DocumentWriteStatus.Ok);

        (await store.UpdateAsync(Edit(first, typeId) with { RegNumber = "П-2" }, Author))
            .Status.ShouldBe(DocumentWriteStatus.RegNumberTaken);
    }

    [Fact(DisplayName = "Невидимый документ правкой не находится — как и чтением")]
    public async Task Invisible_document_is_not_found()
    {
        var (store, types) = await BuildAsync();
        var typeId = (await types.CreateAsync("Поручение", DocumentGroup.Execution, isActive: true))!.Value;
        var documentId = await CreateAsync(store, typeId, "П-1", classification: 4);

        // Допуск ниже грифа: документ невидим, значит и неправим — тот же ответ, что «нет такого».
        var stranger = new AccessContext("60", 0, [5]);
        var result = await store.UpdateAsync(Edit(documentId, typeId), stranger);

        result.Status.ShouldBe(DocumentWriteStatus.NotFound);
    }

    [Fact(DisplayName = "Смена инспектора возвращается в результате — для уведомления обеих сторон")]
    public async Task Inspector_change_is_reported()
    {
        var (store, types) = await BuildAsync();
        var typeId = (await types.CreateAsync("Поручение", DocumentGroup.Execution, isActive: true))!.Value;
        var documentId = await CreateAsync(store, typeId, "П-1");

        var result = await store.UpdateAsync(
            Edit(documentId, typeId) with { InspectorUserId = 8 }, Author);

        result.Status.ShouldBe(DocumentWriteStatus.Ok);
        result.Notice!.PreviousInspectorUserId.ShouldBe(7);
        result.Notice.InspectorUserId.ShouldBe(8);
        result.Notice.ChangedFields.ShouldContain("инспектор");
    }

    // Базовая правка «ничего не меняем» — тесты уточняют её через `with`.
    private static DocumentEdit Edit(int documentId, int typeId) =>
        new(documentId, "П-1", new DateOnly(2026, 2, 1), typeId, DocumentDirection.Incoming, null,
            "Содержание П-1", null, null, DocumentPriority.Medium, 7, 0, 5);

    private static async Task<int> CreateAsync(
        DocumentStore store, int typeId, string regNumber, short classification = 0, int divisionId = 5)
    {
        var result = await store.CreateAsync(
            new DocumentDraft(regNumber, new DateOnly(2026, 2, 1), typeId, DocumentDirection.Incoming,
                null, $"Содержание {regNumber}", null, null, DocumentPriority.Medium, 7,
                classification, divisionId, 42),
            [new AssignmentDraft(divisionId, 11, new DateOnly(2026, 5, 1))],
            useCommonDeadline: false, commonDeadline: null, Author);

        result.Status.ShouldBe(DocumentWriteStatus.Ok);
        return result.DocumentId;
    }

    private async Task<(DocumentStore Store, DocumentTypeStore Types)> BuildAsync()
    {
        var factory = new DocFlowContextFactory(_postgres.GetConnectionString());
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        return (
            new DocumentStore(factory, new NoFileStorage(), new AllowAllAccessPolicy(), TestUserDirectory.AllowAll),
            new DocumentTypeStore(factory));
    }
}
