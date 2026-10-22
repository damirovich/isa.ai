using ISC.AI.Abstractions.Security;
using ISC.AI.Persistence;
using ISC.AI.Persistence.Entities;
using ISC.AI.Persistence.Security;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Testcontainers.PostgreSql;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Учёт сеансов входа (<c>core.user_session</c>, ТФ-АДМ-09, ADR-0060) на настоящем PostgreSQL: миграция ядра,
/// заведение, отметка активности только вперёд, завершение один раз (второй вызов ничего не меняет, причина первого
/// остаётся), список открытых по окну активности и живым соединениям, отбор по имени и IP, сеанс удалённой учётки в
/// списке остаётся. Требуется Docker.
/// </summary>
[Trait("Category", "Gate")]
public sealed class UserSessionStoreTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = TestPostgres.Create();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "Сеанс: заведён действующим, активность — только вперёд, завершается один раз с автором и причиной")]
    public async Task Session_lifecycle()
    {
        var factory = await MigratedAsync();
        var (ivanov, _) = await SeedUsersAsync(factory);
        var store = new UserSessionStore(factory);

        var id = await store.StartAsync(new UserSessionStart(ivanov, "10.0.0.5", new string('x', 400)));
        (await store.IsActiveAsync(id)).ShouldBeTrue();
        (await store.IsActiveAsync(Guid.NewGuid())).ShouldBeFalse();

        var later = DateTime.UtcNow.AddMinutes(5);
        await store.TouchAsync(id, later);
        await store.TouchAsync(id, later.AddMinutes(-3)); // запоздавшая отметка время не откатывает

        (await store.EndAsync(id, UserSessionEndReason.Terminated, ivanov, "  чужой компьютер  ")).ShouldBeTrue();
        (await store.EndAsync(id, UserSessionEndReason.SignedOut, null, null)).ShouldBeFalse();
        (await store.IsActiveAsync(id)).ShouldBeFalse();
        await store.TouchAsync(id, later.AddMinutes(10)); // завершённый не трогается

        await using var db = factory.CreateDbContext();
        var row = await db.UserSessions.SingleAsync(s => s.Id == id);
        row.UserAgent!.Length.ShouldBe(UserSessionEntity.MaxUserAgentLength);
        row.LastActivityAt.ShouldBe(later, TimeSpan.FromMilliseconds(1));
        row.EndReason.ShouldBe(UserSessionEndReason.Terminated);
        row.EndedByUserId.ShouldBe(ivanov);
        row.EndComment.ShouldBe("чужой компьютер");
        row.EndedAt.ShouldNotBeNull();
    }

    [Fact(DisplayName = "Список открытых: окно активности или живое соединение; завершённые — нет; отбор по имени, ФИО и IP; удалённая учётка — в списке")]
    public async Task Open_sessions_are_listed()
    {
        var factory = await MigratedAsync();
        var (ivanov, petrov) = await SeedUsersAsync(factory);
        var store = new UserSessionStore(factory);

        var fresh = await store.StartAsync(new UserSessionStart(ivanov, "10.0.0.5", "Edg/141"));
        var stale = await store.StartAsync(new UserSessionStart(petrov, "10.0.0.9", null));
        var staleConnected = await store.StartAsync(new UserSessionStart(petrov, "10.0.0.10", null));
        var ended = await store.StartAsync(new UserSessionStart(ivanov, "10.0.0.11", null));
        await store.EndAsync(ended, UserSessionEndReason.SignedOut, null, null);

        await using (var db = factory.CreateDbContext())
        {
            // Давно без активности: вне окна.
            await db.UserSessions.Where(s => s.Id == stale || s.Id == staleConnected)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.LastActivityAt, DateTime.UtcNow.AddHours(-3)));

            // Учётку Петрова удалили мягко — его открытые сеансы всё равно видны (их и нужно завершить).
            await db.Users.Where(u => u.Id == petrov).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsDeleted, true));
        }

        var since = DateTime.UtcNow.AddMinutes(-30);
        var all = await store.ListOpenAsync(since, [staleConnected], text: null);
        all.Select(r => r.SessionId).ShouldBe([fresh, staleConnected]); // свежие сверху
        all[0].UserName.ShouldBe("ivanov");
        all[0].DisplayName.ShouldBe("Иванов И. И.");
        all[0].UserAgent.ShouldBe("Edg/141");
        all[1].UserName.ShouldBe("petrov");

        (await store.ListOpenAsync(since, [staleConnected], "ИВАНОВ")).Select(r => r.SessionId).ShouldBe([fresh]);
        (await store.ListOpenAsync(since, [staleConnected], "0.0.10")).Select(r => r.SessionId).ShouldBe([staleConnected]);
        (await store.ListOpenAsync(since, [], "%")).ShouldBeEmpty(); // символы шаблона — буквальные
    }

    private async Task<CoreContextFactory> MigratedAsync()
    {
        var factory = new CoreContextFactory(_postgres.GetConnectionString());
        await using var db = factory.CreateDbContext();
        await db.Database.MigrateAsync();
        return factory;
    }

    private static async Task<(int Ivanov, int Petrov)> SeedUsersAsync(CoreContextFactory factory)
    {
        await using var db = factory.CreateDbContext();
        var ivanov = new AppUserEntity { UserName = "ivanov", DisplayName = "Иванов И. И." };
        var petrov = new AppUserEntity { UserName = "petrov" };
        db.Users.AddRange(ivanov, petrov);
        await db.SaveChangesAsync();
        return (ivanov.Id, petrov.Id);
    }
}
