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
/// Данные отчётов (разд. 6 ТЗ СКИД, этап 5 Э4-35) на реальном PostgreSQL. Требуется Docker.
/// </summary>
/// <remarks>
/// Главная проверка — РАЗГРАНИЧЕНИЕ. Отчёт это массовая выгрузка содержимого документов, и в СКИД
/// разграничения в нём не было вовсе (только проверка роли; понятия грифа там не существовало).
/// Тест закрепляет: отчёт не выдаёт НИ ОДНОЙ строки сверх того, что тому же субъекту показывает
/// список документов. Разойдись эти два правила — утечка пошла бы именно через отчёт, где её
/// труднее всего заметить.
/// </remarks>
public sealed class ReportDataSourceTests : IAsyncLifetime
{
    private static readonly AccessContext Author = new("42", 9, [5, 9]);

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("pgvector/pgvector:pg16").Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "Отчёт разд. 6: выдаёт РОВНО то, что видно в списке документов — ни строкой больше")]
    public async Task Report_never_returns_more_than_the_document_list()
    {
        var (store, reports, typeStore) = await BuildAsync();

        var typeId = await typeStore.CreateAsync("Поручение", DocumentGroup.Execution, isActive: true);
        var period = (From: new DateOnly(2026, 1, 1), To: new DateOnly(2026, 12, 31));

        // Три документа: открытый в своём подразделении, под грифом, и в чужом подразделении.
        await CreateAsync(store, typeId!.Value, "О-1", classification: 0, divisionId: 5);
        await CreateAsync(store, typeId.Value, "С-1", classification: 5, divisionId: 5);
        await CreateAsync(store, typeId.Value, "Д-9", classification: 0, divisionId: 9);

        // Субъект с допуском 0 и только подразделением 5 видит в списке ровно один документ.
        var limited = new AccessContext("60", 0, [5]);
        var visible = (await store.ListAsync(new DocumentListFilter(), limited)).Rows;
        visible.Select(d => d.RegNumber).ShouldBe(["О-1"]);

        // ОТЧЁТ ОБЯЗАН ВИДЕТЬ РОВНО СТОЛЬКО ЖЕ.
        var report = await reports.QueryAsync(
            ReportKind.ByDivision, new ReportFilter(period.From, period.To), limited);
        report.Rows.Select(r => r.RegNumber).Distinct().ShouldBe(["О-1"]);

        // И гриф отчёта — наибольший среди ВЫДАННЫХ строк, а не среди существующих в базе.
        report.MaxClassification.ShouldBe((short)0);

        // Полный допуск — все три документа и гриф 5 (сводка по ДСП сама ДСП, ТБ-032/033).
        var full = await reports.QueryAsync(
            ReportKind.ByDivision, new ReportFilter(period.From, period.To), Author);
        full.Rows.Select(r => r.RegNumber).Distinct().ShouldBe(["О-1", "С-1", "Д-9"], ignoreOrder: true);
        full.MaxClassification.ShouldBe((short)5);
    }

    [Fact(DisplayName = "Отчёт «по срокам» отбирает по СРОКУ, а не по дате регистрации")]
    public async Task Deadline_report_filters_by_deadline_not_registration_date()
    {
        var (store, reports, typeStore) = await BuildAsync();
        var typeId = await typeStore.CreateAsync("Поручение", DocumentGroup.Execution, isActive: true);

        // Документ зарегистрирован в январе, срок — в марте.
        await CreateAsync(store, typeId!.Value, "П-1", classification: 0, divisionId: 5,
            regDate: new DateOnly(2026, 1, 15), deadline: new DateOnly(2026, 3, 20));

        // Окно марта: по дате регистрации документ НЕ попадает, по сроку — попадает.
        var march = new ReportFilter(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31));

        (await reports.QueryAsync(ReportKind.ByDivision, march, Author)).Rows.ShouldBeEmpty();
        (await reports.QueryAsync(ReportKind.ByDeadline, march, Author)).Rows.ShouldHaveSingleItem();

        // Окно января — наоборот. Именно этого различия в СКИД не было: период всегда шёл по
        // дате регистрации, и отчёт «по срокам исполнения» по существу не работал.
        var january = new ReportFilter(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31));
        (await reports.QueryAsync(ReportKind.ByDivision, january, Author)).Rows.ShouldHaveSingleItem();
        (await reports.QueryAsync(ReportKind.ByDeadline, january, Author)).Rows.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Отчёт «просроченные» отбирает только просроченные назначения")]
    public async Task Overdue_report_returns_only_overdue()
    {
        var (store, reports, typeStore) = await BuildAsync();
        var typeId = await typeStore.CreateAsync("Поручение", DocumentGroup.Execution, isActive: true);

        await CreateAsync(store, typeId!.Value, "П-1", 0, 5, deadline: new DateOnly(2026, 3, 1));
        await CreateAsync(store, typeId.Value, "П-2", 0, 5, deadline: new DateOnly(2026, 3, 2));

        var window = new ReportFilter(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));
        (await reports.QueryAsync(ReportKind.Overdue, window, Author)).Rows.ShouldBeEmpty();

        // Система пометила просроченными оба (срок истёк) — теперь отчёт их показывает.
        (await store.MarkOverdueAsync(new DateOnly(2026, 6, 1))).Count.ShouldBe(2);

        var overdue = await reports.QueryAsync(ReportKind.Overdue, window, Author);
        overdue.Rows.Count.ShouldBe(2);
        overdue.Rows.ShouldAllBe(r => r.Status == AssignmentStatus.Overdue);

        // Сортировка по сроку: ближайший первым — это и есть порядок разбора просрочек.
        overdue.Rows.Select(r => r.Deadline).ShouldBeInOrder();
    }

    private static async Task CreateAsync(
        DocumentStore store, int typeId, string regNumber, short classification, int divisionId,
        DateOnly? regDate = null, DateOnly? deadline = null)
    {
        var result = await store.CreateAsync(
            new DocumentDraft(regNumber, regDate ?? new DateOnly(2026, 2, 1), typeId,
                DocumentDirection.Incoming, null, $"Содержание {regNumber}", null, null,
                DocumentPriority.Medium, 7, classification, divisionId, 42),
            [new AssignmentDraft(divisionId, 11, deadline)],
            useCommonDeadline: false, commonDeadline: null,
            new AccessContext("42", 9, [divisionId]));

        result.Status.ShouldBe(DocumentWriteStatus.Ok);
    }

    private async Task<(DocumentStore Store, ReportDataSource Reports, DocumentTypeStore Types)> BuildAsync()
    {
        var factory = new DocFlowContextFactory(_postgres.GetConnectionString());
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        var policy = new AllowAllAccessPolicy();
        return (
            new DocumentStore(factory, new NoFileStorage(), policy, TestUserDirectory.AllowAll),
            new ReportDataSource(factory, policy),
            new DocumentTypeStore(factory));
    }
}
