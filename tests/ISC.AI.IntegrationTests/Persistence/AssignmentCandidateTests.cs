using ISC.AI.Persistence;
using ISC.AI.Persistence.Entities;
using ISC.AI.Profile.Inspector.Data;
using ISC.AI.Profile.Inspector.Domain.Enums;
using ISC.AI.Profile.Inspector.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;
using Shouldly;
using Testcontainers.PostgreSql;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Кандидаты в инспекторы и исполнители (§3.2/§4.1). Требуется Docker.
/// </summary>
/// <remarks>
/// Смысл отбора — не косметика. Исполнитель, которому подразделение назначения не разрешено, НЕ УВИДИТ
/// порученный документ: решётка отфильтрует его на выборке. Поручение будет числиться, а человек
/// о нём не узнает — и выяснится это только по просрочке.
/// </remarks>
public sealed class AssignmentCandidateTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("pgvector/pgvector:pg16").Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "В исполнители попадают только те, кому доступно подразделение назначения")]
    public async Task Assignees_are_limited_by_division()
    {
        var (candidates, roles, factory) = await BuildAsync();

        var inDivision = await CreateUserAsync(factory, "ivanov", "Иванов", divisions: [5]);
        var elsewhere = await CreateUserAsync(factory, "petrov", "Петров", divisions: [9]);
        var disabled = await CreateUserAsync(factory, "sidorov", "Сидоров", divisions: [5], isActive: false);

        await roles.SetRoleAsync(inDivision, UserRole.Performer);
        await roles.SetRoleAsync(elsewhere, UserRole.Performer);
        await roles.SetRoleAsync(disabled, UserRole.Performer);

        var list = await candidates.ListAssigneesAsync(5);

        list.Select(u => u.Id).ShouldBe([inDivision]);
    }

    [Fact(DisplayName = "Руководитель и администратор в исполнители не предлагаются")]
    public async Task Managers_are_not_executors()
    {
        var (candidates, roles, factory) = await BuildAsync();

        var performer = await CreateUserAsync(factory, "p1", "Исполнитель", divisions: [5]);
        var inspector = await CreateUserAsync(factory, "i1", "Инспектор", divisions: [5]);
        var manager = await CreateUserAsync(factory, "m1", "Руководитель", divisions: [5]);
        var admin = await CreateUserAsync(factory, "a1", "Администратор", divisions: [5]);

        await roles.SetRoleAsync(performer, UserRole.Performer);
        await roles.SetRoleAsync(inspector, UserRole.Inspector);
        await roles.SetRoleAsync(manager, UserRole.Manager);
        await roles.SetRoleAsync(admin, UserRole.Administrator);

        var list = await candidates.ListAssigneesAsync(5);

        // Инспектор оставлен намеренно: он ведёт документ и нередко исполняет поручение сам.
        list.Select(u => u.Id).ShouldBe([inspector, performer], ignoreOrder: true);
    }

    [Fact(DisplayName = "В инспекторы попадают только пользователи с ролью «Инспектор»")]
    public async Task Inspectors_are_limited_by_role()
    {
        var (candidates, roles, factory) = await BuildAsync();

        var inspector = await CreateUserAsync(factory, "i1", "Инспектор", divisions: [5]);
        var performer = await CreateUserAsync(factory, "p1", "Исполнитель", divisions: [5]);

        await roles.SetRoleAsync(inspector, UserRole.Inspector);
        await roles.SetRoleAsync(performer, UserRole.Performer);

        (await candidates.ListInspectorsAsync()).Select(u => u.Id).ShouldBe([inspector]);
    }

    /// <summary>
    /// ЗАЩИТА ОТ «ЗАМКА БЕЗ КЛЮЧА» (§6.4.1). Пока роли не назначены никому, сузить список не по чему,
    /// и пустой ответ означал бы, что документ группы «Исполнение» зарегистрировать НЕЛЬЗЯ вовсе:
    /// инспектор для неё обязателен.
    /// </summary>
    [Fact(DisplayName = "Пока роли никому не назначены, списки не пустеют")]
    public async Task Empty_role_registry_does_not_lock_the_system()
    {
        var (candidates, _, factory) = await BuildAsync();

        var first = await CreateUserAsync(factory, "u1", "Первый", divisions: [5]);

        (await candidates.ListInspectorsAsync()).Select(u => u.Id).ShouldBe([first]);
        (await candidates.ListAssigneesAsync(5)).Select(u => u.Id).ShouldBe([first]);
    }

    private static async Task<int> CreateUserAsync(
        IDbContextFactory<CoreDbContext> factory, string login, string name, int[] divisions,
        bool isActive = true)
    {
        await using var db = factory.CreateDbContext();

        var user = new AppUserEntity { UserName = login, DisplayName = name, IsActive = isActive };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        db.Clearances.Add(new ClearanceEntity
        {
            UserId = user.Id,
            MaxClassification = 5,
            DivisionScope = [.. divisions],
        });
        await db.SaveChangesAsync();
        return user.Id;
    }

    private async Task<(AssignmentCandidateDirectory Candidates, IUserRoleStore Roles,
        IDbContextFactory<CoreDbContext> Factory)> BuildAsync()
    {
        var core = new CoreFactory(_postgres.GetConnectionString());
        var inspector = new InspectorFactory(_postgres.GetConnectionString());

        await using (var db = core.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        await using (var db = inspector.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        var roles = new UserRoleStore(core, inspector);
        return (new AssignmentCandidateDirectory(core, roles), roles, core);
    }

    private sealed class InspectorFactory(string connectionString) : IDbContextFactory<InspectorDbContext>
    {
        public InspectorDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<InspectorDbContext>()
                .UseNpgsql(connectionString, npg =>
                    npg.MigrationsHistoryTable("__ef_migrations_history", InspectorDbContext.Schema))
                .UseSnakeCaseNamingConvention()
                .Options);
    }

    private sealed class CoreFactory(string connectionString) : IDbContextFactory<CoreDbContext>
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
