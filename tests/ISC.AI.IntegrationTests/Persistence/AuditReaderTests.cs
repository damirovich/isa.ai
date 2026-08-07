using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Persistence;
using ISC.AI.Persistence.Audit;
using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;
using Shouldly;
using Testcontainers.PostgreSql;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Чтение журнала аудита (ТБ-032) на настоящем PostgreSQL. Требуется Docker.
/// </summary>
/// <remarks>
/// Главное здесь — журнал НЕ ДОЛЖЕН становиться обходным каналом. По одной строке «просмотр
/// документа №17, гриф 3» видно и существование документа, и его гриф, а чувствительная часть записи
/// несёт ещё и содержание события. Поэтому отбор идёт той же решёткой, что и сами данные.
/// </remarks>
public sealed class AuditReaderTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("pgvector/pgvector:pg16").Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "Журнал не выдаёт записей выше допуска и из чужих подразделений (ТБ-032)")]
    public async Task Reader_applies_the_lattice()
    {
        var (writer, reader) = await BuildAsync();

        await writer.WriteAsync(new AuditEntry(AuditAction.View, 0, 7, "doc:1", DivisionId: 5));
        await writer.WriteAsync(new AuditEntry(AuditAction.View, 3, 7, "doc:2", DivisionId: 5));
        await writer.WriteAsync(new AuditEntry(AuditAction.View, 0, 7, "doc:3", DivisionId: 9));

        // Запись без подразделения (вход в систему) — отсекается только грифом.
        await writer.WriteAsync(new AuditEntry(AuditAction.Login, 0, 7));

        var limited = await reader.QueryAsync(new AuditFilter(), new AccessContext("60", 0, [5]));

        limited.Rows.Select(r => r.ObjectRef).ShouldBe([null, "doc:1"], ignoreOrder: true);
        limited.TotalCount.ShouldBe(2);

        var full = await reader.QueryAsync(new AuditFilter(), new AccessContext("42", 9, [5, 9]));
        full.TotalCount.ShouldBe(4);
    }

    [Fact(DisplayName = "Журнал фильтруется по действию, субъекту и объекту")]
    public async Task Reader_applies_filters()
    {
        var (writer, reader) = await BuildAsync();
        var access = new AccessContext("42", 9, [5]);

        await writer.WriteAsync(new AuditEntry(AuditAction.View, 0, 7, "docflow:document:1", DivisionId: 5));
        await writer.WriteAsync(new AuditEntry(AuditAction.Export, 0, 7, "report:ByAssignee", DivisionId: 5));
        await writer.WriteAsync(new AuditEntry(AuditAction.Export, 0, 8, "report:Overdue", DivisionId: 5));

        (await reader.QueryAsync(new AuditFilter(Action: AuditAction.Export), access)).TotalCount.ShouldBe(2);
        (await reader.QueryAsync(new AuditFilter(SubjectId: 8), access)).TotalCount.ShouldBe(1);

        // Поиск по объекту — без учёта регистра (ILIKE): администратор ищет «report», а не «Report».
        var byObject = await reader.QueryAsync(new AuditFilter(ObjectRef: "REPORT"), access);
        byObject.TotalCount.ShouldBe(2);
    }

    [Fact(DisplayName = "Журнал отдаётся страницами, новые записи первыми")]
    public async Task Reader_pages_newest_first()
    {
        var (writer, reader) = await BuildAsync();
        var access = new AccessContext("42", 9, [5]);

        for (var i = 1; i <= 5; i++)
        {
            await writer.WriteAsync(new AuditEntry(AuditAction.View, 0, 7, $"doc:{i}", DivisionId: 5));
        }

        var first = await reader.QueryAsync(new AuditFilter(Page: 1, PageSize: 2), access);
        first.TotalCount.ShouldBe(5);
        first.Rows.Select(r => r.ObjectRef).ShouldBe(["doc:5", "doc:4"]);

        var second = await reader.QueryAsync(new AuditFilter(Page: 2, PageSize: 2), access);
        second.Rows.Select(r => r.ObjectRef).ShouldBe(["doc:3", "doc:2"]);

        // Потолок размера страницы: «покажи всё» журнал не выдержит.
        var capped = await reader.QueryAsync(new AuditFilter(PageSize: 100_000), access);
        capped.Rows.Count.ShouldBeLessThanOrEqualTo(AuditReader.MaxPageSize);
    }

    private async Task<(AuditWriter Writer, AuditReader Reader)> BuildAsync()
    {
        var factory = new TestContextFactory(_postgres.GetConnectionString());
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        return (new AuditWriter(factory), new AuditReader(factory));
    }

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
