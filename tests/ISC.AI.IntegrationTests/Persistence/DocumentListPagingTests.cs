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
/// Постраничность и полный набор фильтров реестра (§3.4 ТЗ СКИД) на настоящем PostgreSQL.
/// Требуется Docker.
/// </summary>
/// <remarks>
/// Раньше реестр отдавался целиком: на демонстрации это незаметно, а на реальном корпусе означает,
/// что каждое открытие списка тянет из БД тысячи строк вместе с кратким содержанием каждой.
/// Проверяется и то, что общее число считается ПО ВСЕЙ выборке, а не по странице: иначе навигация
/// показывала бы одну страницу там, где их двадцать.
/// </remarks>
public sealed class DocumentListPagingTests : IAsyncLifetime
{
    private static readonly AccessContext Access = new("42", 9, [5, 9]);

    private readonly PostgreSqlContainer _postgres = TestPostgres.Create();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "Реестр отдаётся страницами, общее число — по всей выборке")]
    public async Task List_is_paged_and_reports_total()
    {
        var (store, types) = await BuildAsync();
        var typeId = (await types.CreateAsync("Поручение", DocumentGroup.Execution, isActive: true))!.Value;

        for (var i = 1; i <= 7; i++)
        {
            await CreateAsync(store, typeId, $"П-{i}", regDate: new DateOnly(2026, 1, i));
        }

        var first = await store.ListAsync(new DocumentListFilter(Page: 1, PageSize: 3), Access);
        first.Rows.Count.ShouldBe(3);
        first.TotalCount.ShouldBe(7);

        // Порядок — новые первыми, значит на первой странице самые поздние даты регистрации.
        first.Rows.Select(r => r.RegNumber).ShouldBe(["П-7", "П-6", "П-5"]);

        var third = await store.ListAsync(new DocumentListFilter(Page: 3, PageSize: 3), Access);
        third.Rows.Select(r => r.RegNumber).ShouldBe(["П-1"]);
        third.TotalCount.ShouldBe(7);

        // Потолок размера страницы: «дай сто тысяч строк» вернуло бы весь корпус.
        var capped = await store.ListAsync(new DocumentListFilter(PageSize: 100_000), Access);
        capped.Rows.Count.ShouldBeLessThanOrEqualTo(DocumentStore.MaxPageSize);
    }

    [Fact(DisplayName = "Общее число считается ПОСЛЕ фильтров и в пределах допуска")]
    public async Task Total_respects_filters_and_clearance()
    {
        var (store, types) = await BuildAsync();
        var typeId = (await types.CreateAsync("Поручение", DocumentGroup.Execution, isActive: true))!.Value;

        await CreateAsync(store, typeId, "О-1", classification: 0, divisionId: 5);
        await CreateAsync(store, typeId, "О-2", classification: 0, divisionId: 5);
        await CreateAsync(store, typeId, "С-1", classification: 5, divisionId: 5);
        await CreateAsync(store, typeId, "Д-9", classification: 0, divisionId: 9);

        (await store.ListAsync(new DocumentListFilter(), Access)).TotalCount.ShouldBe(4);

        // Допуск 0 и только подразделение 5: и строки, и ЧИСЛО обязаны сузиться. Если бы общее число
        // считалось до решётки, оно выдавало бы существование скрытых документов (ТБ-020/021).
        var limited = new AccessContext("60", 0, [5]);
        var page = await store.ListAsync(new DocumentListFilter(), limited);
        page.TotalCount.ShouldBe(2);
        page.Rows.Count.ShouldBe(2);

        (await store.ListAsync(new DocumentListFilter(DivisionId: 9), Access)).TotalCount.ShouldBe(1);
    }

    [Fact(DisplayName = "Поиск идёт и по источнику, не только по номеру и содержанию")]
    public async Task Text_search_covers_source()
    {
        var (store, types) = await BuildAsync();
        var typeId = (await types.CreateAsync("Поручение", DocumentGroup.Execution, isActive: true))!.Value;

        await CreateAsync(store, typeId, "П-1", source: "Министерство финансов");
        await CreateAsync(store, typeId, "П-2", source: "Аппарат правительства");

        // Документы часто ищут именно по отправителю, а его название в краткое содержание
        // попадает не всегда — в СКИД источник в поиске участвовал.
        var found = await store.ListAsync(new DocumentListFilter(Text: "министерство"), Access);
        found.Rows.ShouldHaveSingleItem().RegNumber.ShouldBe("П-1");
    }

    [Fact(DisplayName = "Фильтры по группе, приоритету и инспектору сужают выдачу")]
    public async Task Group_priority_and_inspector_filters_work()
    {
        var (store, types) = await BuildAsync();
        var execution = (await types.CreateAsync("Поручение", DocumentGroup.Execution, isActive: true))!.Value;
        var storage = (await types.CreateAsync("Справка", DocumentGroup.Storage, isActive: true))!.Value;

        await CreateAsync(store, execution, "П-1", priority: DocumentPriority.High, inspectorUserId: 7);
        await CreateAsync(store, execution, "П-2", priority: DocumentPriority.Low, inspectorUserId: 8);
        await CreateStorageAsync(store, storage, "Х-1");

        (await store.ListAsync(new DocumentListFilter(Group: DocumentGroup.Storage), Access))
            .Rows.ShouldHaveSingleItem().RegNumber.ShouldBe("Х-1");

        (await store.ListAsync(new DocumentListFilter(Priority: DocumentPriority.High), Access))
            .Rows.ShouldHaveSingleItem().RegNumber.ShouldBe("П-1");

        (await store.ListAsync(new DocumentListFilter(InspectorUserId: 8), Access))
            .Rows.ShouldHaveSingleItem().RegNumber.ShouldBe("П-2");
    }

    private static async Task CreateAsync(
        DocumentStore store, int typeId, string regNumber, short classification = 0, int divisionId = 5,
        DateOnly? regDate = null, string? source = null,
        DocumentPriority priority = DocumentPriority.Medium, int inspectorUserId = 7)
    {
        var result = await store.CreateAsync(
            new DocumentDraft(regNumber, regDate ?? new DateOnly(2026, 2, 1), typeId,
                DocumentDirection.Incoming, source, $"Содержание {regNumber}", null, null,
                priority, inspectorUserId, classification, divisionId, 42),
            [new AssignmentDraft(divisionId, 11, new DateOnly(2026, 5, 1))],
            useCommonDeadline: false, commonDeadline: null, Access);

        result.Status.ShouldBe(DocumentWriteStatus.Ok);
    }

    // У «Хранения» нет ни приоритета, ни инспектора, ни назначений (§1.4).
    private static async Task CreateStorageAsync(DocumentStore store, int typeId, string regNumber)
    {
        var result = await store.CreateAsync(
            new DocumentDraft(regNumber, new DateOnly(2026, 2, 1), typeId, DocumentDirection.Internal,
                null, $"Содержание {regNumber}", null, null, null, null, 0, 5, 42),
            [], useCommonDeadline: false, commonDeadline: null, Access);

        result.Status.ShouldBe(DocumentWriteStatus.Ok);
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
