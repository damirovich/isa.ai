using ISC.AI.Abstractions.Audit;
using ISC.AI.IntegrationTests.Persistence;
using ISC.AI.Persistence;
using ISC.AI.Persistence.Audit;
using ISC.AI.Persistence.Security;
using ISC.AI.Web.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Testcontainers.PostgreSql;

namespace ISC.AI.IntegrationTests.Security;

/// <summary>
/// Блокировка учётной записи после неудачных входов подряд (ТБ-081, ADR-0065) на настоящем PostgreSQL: настоящий вход
/// (<see cref="LoginService"/> + локальная идентичность), настоящий журнал аудита. Пятая ошибка подряд блокирует; с верным
/// паролем после этого — тот же отказ; снять блокировку может Администратор (и сброс пароля); параллельные попытки не
/// теряют приращений и блокируют ровно один раз.
/// </summary>
/// <remarks>Требуется Docker. Свой контейнер на тест: журнал аудита проверяется подсчётом записей.</remarks>
[Trait("Category", "Gate")]
public sealed class AccountLockoutTests : IAsyncLifetime
{
    private const string Password = "Верный-пароль-2026";
    private const string WrongPassword = "неверный";

    private readonly PostgreSqlContainer _postgres = TestPostgres.Create();
    private CoreContextFactory _core = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _core = new CoreContextFactory(_postgres.GetConnectionString());
        await using var db = _core.CreateDbContext();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "ТБ-081: пятая неудача подряд блокирует; с верным паролем после этого — тот же отказ; в журнале — попытки, одна блокировка и «locked»")]
    public async Task Fifth_failure_locks_and_correct_password_is_refused()
    {
        var userId = await CreateAccountAsync("sidorov");

        for (var i = 1; i <= 4; i++)
        {
            (await Login("sidorov", WrongPassword)).ShouldBeNull();
        }

        (await StateAsync(userId)).ShouldBe((4, false));

        (await Login("sidorov", WrongPassword)).ShouldBeNull();
        (await StateAsync(userId)).ShouldBe((5, true));

        // Пароль подобран — но вход тот же отказ, что и на неверный пароль: подборщик об этом не узнаёт.
        (await Login("sidorov", Password)).ShouldBeNull();

        // Заблокированная учётка счётчик больше не растит.
        (await Login("sidorov", WrongPassword)).ShouldBeNull();
        (await StateAsync(userId)).ShouldBe((5, true));

        await using var db = _core.CreateDbContext();
        (await db.AuditRecords.CountAsync(r => r.Action == AuditAction.Login && r.ObjectRef == "login=sidorov;result=denied")).ShouldBe(5);
        (await db.AuditRecords.CountAsync(r => r.Action == AuditAction.Login && r.ObjectRef == "login=sidorov;result=locked")).ShouldBe(2);
        var lockout = await db.AuditRecords.SingleAsync(r => r.Action == AuditAction.Modify && r.ObjectRef == LoginLockout.LockoutObjectRef(userId));
        lockout.PayloadSensitive.ShouldBe(LoginLockout.LockoutObjectRef(userId) + ":attempts=5");
        lockout.SubjectId.ShouldBeNull(); // блокирует система, а не человек

        // ТБ-080: каждая неудачная попытка — исход «отказ», в том числе на заблокированной учётке; сеанса у отказа нет.
        var attempts = await db.AuditRecords.Where(r => r.Action == AuditAction.Login).ToListAsync();
        attempts.Count.ShouldBe(7);
        attempts.ShouldAllBe(r => r.Outcome == AuditOutcome.Denied && r.SessionId == null);
    }

    [Fact(DisplayName = "ТБ-080: запись успешного входа — исход «успех» и номер сеанса, который откроется")]
    public async Task Granted_login_record_carries_the_planned_session()
    {
        await CreateAccountAsync("orlova");
        var planned = Guid.NewGuid();

        var audit = new AuditWriter(_core);
        var service = new LoginService(
            new LocalIdentityProvider(_core), _core, audit, new LoginLockout(_core, audit), NullLogger<LoginService>.Instance);
        (await service.AuthenticateAsync("orlova", Password, planned)).ShouldNotBeNull();

        await using var db = _core.CreateDbContext();
        var granted = await db.AuditRecords.SingleAsync(r => r.Action == AuditAction.Login && r.ObjectRef == "login=orlova;result=granted");
        granted.Outcome.ShouldBe(AuditOutcome.Success);
        granted.SessionId.ShouldBe(planned);
    }

    [Fact(DisplayName = "ТБ-081: успешный вход обнуляет счётчик — блокируют только 5 неудач ПОДРЯД")]
    public async Task Success_resets_the_counter()
    {
        var userId = await CreateAccountAsync("petrova");

        for (var i = 0; i < 4; i++)
        {
            await Login("petrova", WrongPassword);
        }

        (await Login("petrova", Password)).ShouldNotBeNull();
        (await StateAsync(userId)).ShouldBe((0, false));

        for (var i = 0; i < 4; i++)
        {
            await Login("petrova", WrongPassword);
        }

        (await StateAsync(userId)).ShouldBe((4, false));
        (await Login("petrova", Password)).ShouldNotBeNull();
    }

    [Fact(DisplayName = "ТБ-081: Администратор снимает блокировку — вход с верным паролем снова проходит; сброс пароля тоже снимает блокировку")]
    public async Task Administrator_unlock_and_password_reset_restore_access()
    {
        var accounts = new UserAccountStore(_core);
        var userId = await CreateAccountAsync("kuznetsov");
        await LockAsync("kuznetsov");

        (await accounts.UnlockAsync(userId)).ShouldBeTrue();
        (await StateAsync(userId)).ShouldBe((0, false));
        (await Login("kuznetsov", Password)).ShouldNotBeNull();

        await LockAsync("kuznetsov");
        (await accounts.SearchAsync(new(Text: "kuznetsov"))).Rows.Single().IsLockedOut.ShouldBeTrue();

        (await accounts.ResetPasswordAsync(userId, "Временный-2026")).ShouldBeTrue();
        (await StateAsync(userId)).ShouldBe((0, false));
        (await Login("kuznetsov", "Временный-2026")).ShouldNotBeNull();

        (await accounts.UnlockAsync(999_999)).ShouldBeFalse();
    }

    [Fact(DisplayName = "ТБ-081: неизвестное имя входа ничего не блокирует и учётку не создаёт — ответ тот же отказ")]
    public async Task Unknown_login_changes_nothing()
    {
        for (var i = 0; i < 7; i++)
        {
            (await Login("nobody", WrongPassword)).ShouldBeNull();
        }

        await using var db = _core.CreateDbContext();
        (await db.Users.AnyAsync(u => u.UserName == "nobody")).ShouldBeFalse();
        (await db.AuditRecords.AnyAsync(r => r.Action == AuditAction.Modify && r.ObjectRef!.StartsWith("auth:account:"))).ShouldBeFalse();
        (await db.AuditRecords.CountAsync(r => r.Action == AuditAction.Login && r.ObjectRef == "login=nobody;result=denied")).ShouldBe(7);
    }

    [Fact(DisplayName = "ТБ-081: параллельные неудачи не теряют приращений — счётчик ровно 5 и одна запись о блокировке")]
    public async Task Parallel_failures_lock_exactly_once()
    {
        var userId = await CreateAccountAsync("volkov");

        // Каждая попытка — свой «запрос»: свой экземпляр входа и журнала, как в хосте.
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => Login("volkov", WrongPassword))));

        results.ShouldAllBe(p => p == null);
        (await StateAsync(userId)).ShouldBe((5, true));
        await using var db = _core.CreateDbContext();
        (await db.AuditRecords.CountAsync(r => r.Action == AuditAction.Modify && r.ObjectRef == LoginLockout.LockoutObjectRef(userId))).ShouldBe(1);
    }

    private Task<System.Security.Claims.ClaimsPrincipal?> Login(string login, string password)
    {
        var audit = new AuditWriter(_core);
        var service = new LoginService(
            new LocalIdentityProvider(_core), _core, audit, new LoginLockout(_core, audit), NullLogger<LoginService>.Instance);
        return service.AuthenticateAsync(login, password);
    }

    private async Task LockAsync(string login)
    {
        for (var i = 0; i < LoginLockout.DefaultThreshold; i++)
        {
            await Login(login, WrongPassword);
        }
    }

    // Учётка — тем же путём, что у Администратора: хеш пароля, штамп и привязка ExternalId.
    private async Task<int> CreateAccountAsync(string login) =>
        (await new UserAccountStore(_core).CreateAsync(login, displayName: login, Password)).ShouldNotBeNull();

    private async Task<(int Failed, bool Locked)> StateAsync(int userId)
    {
        await using var db = _core.CreateDbContext();
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
        return (user.FailedLoginCount, user.LockedOutAt is not null);
    }
}
