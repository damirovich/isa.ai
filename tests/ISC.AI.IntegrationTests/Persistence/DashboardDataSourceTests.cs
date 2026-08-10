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
/// Данные дашборда документооборота на настоящем PostgreSQL. Требуется Docker.
/// </summary>
/// <remarks>
/// Главная проверка — та же, что у отчётов, но опаснее: АГРЕГАТ УТЕКАЕТ ЧИСЛОМ. Строку недоступного
/// документа видно сразу, а «просрочено: 7» при пустом реестре выглядит безобидно и при этом сообщает
/// о существовании семи недоступных поручений. Поэтому счётчики обязаны считать ровно то множество,
/// которое субъект видит в списке документов.
/// </remarks>
public sealed class DashboardDataSourceTests : IAsyncLifetime
{
    private static readonly AccessContext Author = new("42", 9, [5, 9]);
    private static readonly DateOnly Today = new(2026, 6, 1);

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("pgvector/pgvector:pg16").Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "Дашборд не считает того, чего не показывает список документов")]
    public async Task Counters_never_exceed_the_visible_list()
    {
        var (store, dashboard, types) = await BuildAsync();
        var typeId = (await types.CreateAsync("Поручение", DocumentGroup.Execution, isActive: true))!.Value;

        await CreateAsync(store, typeId, "О-1", classification: 0, divisionId: 5);
        await CreateAsync(store, typeId, "С-1", classification: 5, divisionId: 5);
        await CreateAsync(store, typeId, "Д-9", classification: 0, divisionId: 9);

        // Субъект с допуском 0 и подразделением 5 видит в реестре ровно один документ.
        var limited = new AccessContext("60", 0, [5]);
        (await store.ListAsync(new DocumentListFilter(), limited)).Rows.Count.ShouldBe(1);

        var data = await dashboard.GetAsync(limited, Today, horizonDays: 7);

        data.All.Active.ShouldBe(1);
        data.ByDivision.ShouldHaveSingleItem().DivisionId.ShouldBe(5);
        data.Upcoming.Select(u => u.RegNumber).ShouldBe(["О-1"]);

        // Полный допуск — все три.
        var full = await dashboard.GetAsync(Author, Today, horizonDays: 7);
        full.All.Active.ShouldBe(3);
        full.ByDivision.Count.ShouldBe(2);
    }

    [Fact(DisplayName = "Просроченные считаются отдельно от «в работе», а не растворяются в них")]
    public async Task Overdue_is_counted_separately()
    {
        var (store, dashboard, types) = await BuildAsync();
        var typeId = (await types.CreateAsync("Поручение", DocumentGroup.Execution, isActive: true))!.Value;

        await CreateAsync(store, typeId, "П-1", deadline: new DateOnly(2026, 5, 1));
        await CreateAsync(store, typeId, "П-2", deadline: new DateOnly(2026, 7, 1));

        (await store.MarkOverdueAsync(Today)).Count.ShouldBe(1);

        var data = await dashboard.GetAsync(Author, Today, horizonDays: 7);

        data.All.Overdue.ShouldBe(1);
        data.All.Active.ShouldBe(1);
        data.ByDivision.ShouldHaveSingleItem().Overdue.ShouldBe(1);
    }

    [Fact(DisplayName = "«Срок сегодня» и «срок скоро» разделены горизонтом")]
    public async Task Due_today_and_due_soon_are_split()
    {
        var (store, dashboard, types) = await BuildAsync();
        var typeId = (await types.CreateAsync("Поручение", DocumentGroup.Execution, isActive: true))!.Value;

        await CreateAsync(store, typeId, "СЕГОДНЯ", deadline: Today);
        await CreateAsync(store, typeId, "СКОРО", deadline: Today.AddDays(3));
        await CreateAsync(store, typeId, "ПОТОМ", deadline: Today.AddDays(60));

        var data = await dashboard.GetAsync(Author, Today, horizonDays: 7);

        data.All.DueToday.ShouldBe(1);
        data.All.DueSoon.ShouldBe(1);

        // «Потом» в ближайшие сроки не попадает — иначе список перестал бы быть списком срочного.
        data.Upcoming.Select(u => u.RegNumber).ShouldBe(["СЕГОДНЯ", "СКОРО"]);
    }

    [Fact(DisplayName = "Личный срез считает поручения, где субъект исполнитель или инспектор")]
    public async Task Mine_counts_own_assignments()
    {
        var (store, dashboard, types) = await BuildAsync();
        var typeId = (await types.CreateAsync("Поручение", DocumentGroup.Execution, isActive: true))!.Value;

        // Инспектор 7, исполнитель 11 — как в CreateAsync.
        await CreateAsync(store, typeId, "П-1");
        await CreateAsync(store, typeId, "П-2", assigneeUserId: 99);

        // Субъект 11 — исполнитель только по первому.
        var assignee = new AccessContext("11", 9, [5]);
        (await dashboard.GetAsync(assignee, Today, 7)).Mine.Active.ShouldBe(1);

        // Субъект 7 — инспектор ОБОИХ документов, значит видит оба и в личном срезе.
        var inspector = new AccessContext("7", 9, [5]);
        (await dashboard.GetAsync(inspector, Today, 7)).Mine.Active.ShouldBe(2);

        // Посторонний: доступ есть, но лично его не касается ничего.
        var other = new AccessContext("60", 9, [5]);
        var data = await dashboard.GetAsync(other, Today, 7);
        data.All.Active.ShouldBe(2);
        data.Mine.Active.ShouldBe(0);
    }

    private static async Task CreateAsync(
        DocumentStore store, int typeId, string regNumber, short classification = 0, int divisionId = 5,
        DateOnly? deadline = null, int? assigneeUserId = 11)
    {
        var result = await store.CreateAsync(
            new DocumentDraft(regNumber, new DateOnly(2026, 2, 1), typeId, DocumentDirection.Incoming,
                null, $"Содержание {regNumber}", null, null, DocumentPriority.Medium, 7,
                classification, divisionId, 42),
            [new AssignmentDraft(divisionId, assigneeUserId, deadline ?? new DateOnly(2026, 6, 5))],
            useCommonDeadline: false, commonDeadline: null, Author);

        result.Status.ShouldBe(DocumentWriteStatus.Ok);
    }

    private async Task<(DocumentStore Store, DashboardDataSource Dashboard, DocumentTypeStore Types)> BuildAsync()
    {
        var factory = new DocFlowContextFactory(_postgres.GetConnectionString());
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        var policy = new AllowAllAccessPolicy();
        return (
            new DocumentStore(factory, new NoFileStorage(), policy, TestUserDirectory.AllowAll),
            new DashboardDataSource(factory, policy),
            new DocumentTypeStore(factory));
    }
}
