using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Persistence;
using ISC.AI.Persistence.Entities;
using ISC.AI.Web.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Pgvector.EntityFrameworkCore;
using Shouldly;
using Testcontainers.PostgreSql;

namespace ISC.AI.IntegrationTests.Security;

/// <summary>
/// Э3-08 (ADR-0016): привязка к локальному субъекту СТРОГО по <c>ExternalId</c>, никогда по имени
/// входа — регрессия на находку ревью «JIT-привязка по UserName перепривязывает чужую учётку и
/// наследует её допуск» (обход default-deny, ТБ-012). Реальный PostgreSQL через Testcontainers.
/// </summary>
/// <remarks>Требуется Docker.</remarks>
[Trait("Category", "Gate")]
public sealed class LoginServiceTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("pgvector/pgvector:pg16").Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "Переиспользованный логин с чужим ExternalId НЕ наследует допуск — вход отклонён")]
    public async Task Reused_login_with_foreign_ExternalId_is_rejected_not_rebound()
    {
        var factory = new TestContextFactory(_postgres.GetConnectionString());
        int predecessorUserId;
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();

            // Предшественник: локальная учётка с назначенным допуском.
            var predecessor = new AppUserEntity
            {
                UserName = "ivanov",
                ExternalId = "ext-old-guid",
                Clearance = new ClearanceEntity { MaxClassification = 2, DivisionScope = [7] },
            };
            db.Users.Add(predecessor);
            await db.SaveChangesAsync();
            predecessorUserId = predecessor.Id;
        }

        // Во внешней системе логин "ivanov" переиспользован НОВЫМ человеком (новый ExternalId).
        var identityProvider = Substitute.For<IExternalIdentityProvider>();
        identityProvider.VerifyCredentialsAsync("ivanov", "any-password", Arg.Any<CancellationToken>())
            .Returns(new ExternalIdentity("ext-new-guid", "ivanov", "Новый Сотрудник", null, "stamp-1"));

        var auditWriter = Substitute.For<IAuditWriter>();
        var service = new LoginService(
            identityProvider, factory, auditWriter, NullLogger<LoginService>.Instance);

        var principal = await service.AuthenticateAsync("ivanov", "any-password");

        // Единый отказ — новый человек НЕ вошёл под чужой учёткой.
        principal.ShouldBeNull();

        await using var verify = factory.CreateDbContext();
        // Предшественник не тронут: ExternalId и допуск остались его собственными.
        var predecessor2 = await verify.Users.Include(u => u.Clearance)
            .SingleAsync(u => u.Id == predecessorUserId);
        predecessor2.ExternalId.ShouldBe("ext-old-guid");
        predecessor2.Clearance!.MaxClassification.ShouldBe<short>(2);

        // Новый человек НЕ получил локальную учётку автоматически (не JIT-создан в обход конфликта).
        (await verify.Users.CountAsync(u => u.ExternalId == "ext-new-guid")).ShouldBe(0);
    }

    [Fact(DisplayName = "Новый ExternalId без коллизии имени — JIT-создание БЕЗ допуска (default-deny)")]
    public async Task New_external_id_without_collision_creates_user_without_clearance()
    {
        var factory = new TestContextFactory(_postgres.GetConnectionString());
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        var identityProvider = Substitute.For<IExternalIdentityProvider>();
        identityProvider.VerifyCredentialsAsync("petrov", "any-password", Arg.Any<CancellationToken>())
            .Returns(new ExternalIdentity("ext-petrov", "petrov", "Пётр Петров", null, "stamp-1"));

        var auditWriter = Substitute.For<IAuditWriter>();
        var service = new LoginService(
            identityProvider, factory, auditWriter, NullLogger<LoginService>.Instance);

        var principal = await service.AuthenticateAsync("petrov", "any-password");

        principal.ShouldNotBeNull();

        await using var verify = factory.CreateDbContext();
        var user = await verify.Users.Include(u => u.Clearance).SingleAsync(u => u.ExternalId == "ext-petrov");
        user.Clearance.ShouldBeNull(); // default-deny: retrieval невозможен до назначения допуска
    }

    // Контекст с теми же опциями, что в проде (snake_case + pgvector).
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
