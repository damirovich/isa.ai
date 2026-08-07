using ISC.AI.Abstractions.Security;
using ISC.AI.Persistence;
using ISC.AI.Persistence.Security;
using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;
using Shouldly;
using Testcontainers.PostgreSql;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Поиск и правка учётных записей (§6.5, перенос списка пользователей СКИД) на настоящем PostgreSQL.
/// Требуется Docker.
/// </summary>
public sealed class UserAccountSearchTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("pgvector/pgvector:pg16").Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "Поиск идёт по логину, ФИО и должности")]
    public async Task Search_covers_login_name_and_position()
    {
        var store = await BuildAsync();

        var ivanov = await store.CreateAsync("i.ivanov", "Иванов Иван", "Vremenniy-1");
        await store.UpdateProfileAsync(ivanov!.Value, "Иванов Иван", "старший инспектор");
        var petrov = await store.CreateAsync("p.petrov", "Петров Пётр", "Vremenniy-2");
        await store.UpdateProfileAsync(petrov!.Value, "Петров Пётр", "начальник отдела");

        (await store.SearchAsync(new UserAccountFilter(Text: "ivanov")))
            .Rows.ShouldHaveSingleItem().UserName.ShouldBe("i.ivanov");

        (await store.SearchAsync(new UserAccountFilter(Text: "Петров")))
            .Rows.ShouldHaveSingleItem().UserName.ShouldBe("p.petrov");

        // Однофамильцев различают именно должностью — ровно поэтому она искалась и в СКИД.
        (await store.SearchAsync(new UserAccountFilter(Text: "начальник")))
            .Rows.ShouldHaveSingleItem().UserName.ShouldBe("p.petrov");
    }

    [Fact(DisplayName = "Список отдаётся страницами, общее число — по всей выборке")]
    public async Task Search_is_paged()
    {
        var store = await BuildAsync();
        for (var i = 1; i <= 5; i++)
        {
            await store.CreateAsync($"user{i}", $"Пользователь {i}", $"Vremenniy-{i}");
        }

        var first = await store.SearchAsync(new UserAccountFilter(Page: 1, PageSize: 2));
        first.Rows.Count.ShouldBe(2);
        first.TotalCount.ShouldBe(5);

        var last = await store.SearchAsync(new UserAccountFilter(Page: 3, PageSize: 2));
        last.Rows.ShouldHaveSingleItem();
    }

    /// <summary>
    /// Пустой набор идентификаторов означает «под эту роль никто не подошёл». Прими его за
    /// «ограничения нет» — фильтр по незанятой роли показал бы ВЕСЬ список, то есть ответ,
    /// противоположный запрошенному.
    /// </summary>
    [Fact(DisplayName = "Пустое ограничение по идентификаторам даёт пустую выдачу, а не весь список")]
    public async Task Empty_restriction_yields_nothing()
    {
        var store = await BuildAsync();
        await store.CreateAsync("user1", "Пользователь 1", "Vremenniy-1");

        (await store.SearchAsync(new UserAccountFilter())).TotalCount.ShouldBe(1);
        (await store.SearchAsync(new UserAccountFilter(RestrictToUserIds: []))).TotalCount.ShouldBe(0);
    }

    [Fact(DisplayName = "Отключение сохраняет причину и обрывает сессии, включение причину не стирает")]
    public async Task Deactivation_keeps_reason()
    {
        var store = await BuildAsync();
        var userId = (await store.CreateAsync("user1", "Пользователь 1", "Vremenniy-1"))!.Value;

        var before = (await store.SearchAsync(new UserAccountFilter())).Rows.Single();
        before.IsActive.ShouldBeTrue();

        (await store.SetActiveAsync(userId, isActive: false, reason: "уволен")).ShouldBeTrue();

        var disabled = (await store.SearchAsync(new UserAccountFilter())).Rows.Single();
        disabled.IsActive.ShouldBeFalse();
        disabled.DeactivationReason.ShouldBe("уволен");

        // Причина остаётся и после повторного включения — как след того, что отключение было.
        (await store.SetActiveAsync(userId, isActive: true)).ShouldBeTrue();
        var enabled = (await store.SearchAsync(new UserAccountFilter())).Rows.Single();
        enabled.IsActive.ShouldBeTrue();
        enabled.DeactivationReason.ShouldBe("уволен");

        (await store.SearchAsync(new UserAccountFilter(IsActive: false))).TotalCount.ShouldBe(0);
    }

    /// <summary>
    /// Правка подписи НЕ должна менять штамп безопасности: иначе исправление опечатки в фамилии
    /// выкидывало бы человека из системы — наказание без причины.
    /// </summary>
    [Fact(DisplayName = "Правка ФИО и должности не обрывает сессию пользователя")]
    public async Task Profile_update_keeps_security_stamp()
    {
        var factory = new TestContextFactory(_postgres.GetConnectionString());
        var store = await BuildAsync();
        var userId = (await store.CreateAsync("user1", "Иванов", "Vremenniy-1"))!.Value;

        string? StampAsync()
        {
            using var db = factory.CreateDbContext();
            return db.Users.Single(u => u.Id == userId).SecurityStamp;
        }

        var before = StampAsync();
        (await store.UpdateProfileAsync(userId, "Иванов Иван", "инспектор")).ShouldBeTrue();
        StampAsync().ShouldBe(before);

        var row = (await store.SearchAsync(new UserAccountFilter())).Rows.Single();
        row.DisplayName.ShouldBe("Иванов Иван");
        row.Position.ShouldBe("инспектор");
    }

    private async Task<UserAccountStore> BuildAsync()
    {
        var factory = new TestContextFactory(_postgres.GetConnectionString());
        await using var db = factory.CreateDbContext();
        await db.Database.MigrateAsync();
        return new UserAccountStore(factory);
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
