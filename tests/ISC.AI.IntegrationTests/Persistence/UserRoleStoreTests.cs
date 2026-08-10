using ISC.AI.Persistence;
using ISC.AI.Persistence.Entities;
using ISC.AI.Profile.Inspector.Data;
using ISC.AI.Profile.Inspector.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;
using Shouldly;
using Testcontainers.PostgreSql;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Реестр ролей (§2.1 ТЗ СКИД) и признак РЕЖИМА ПЕРВИЧНОЙ НАСТРОЙКИ на реальном PostgreSQL.
/// Режим существует потому, что без него система запирается: страница ролей требует Администратора,
/// а назначить первого Администратора без неё нельзя (эта ловушка сработала у заказчика сразу после
/// выпуска этапа 6.4). Тест фиксирует ОБА перехода — закрытие и осознанное переоткрытие.
/// </summary>
public sealed class UserRoleStoreTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = TestPostgres.Create();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "Роли: режим первичной настройки держится на ОТСУТСТВИИ Администратора, не на пустом реестре")]
    public async Task Bootstrap_mode_tracks_absence_of_administrator()
    {
        var coreFactory = new CoreContextFactory(_postgres.GetConnectionString());
        var inspectorFactory = new InspectorContextFactory(_postgres.GetConnectionString());
        await using (var db = coreFactory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        await using (var db = inspectorFactory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        int userId;
        await using (var core = coreFactory.CreateDbContext())
        {
            var user = new AppUserEntity { UserName = "operator", DisplayName = "Оператор О.О." };
            core.Users.Add(user);
            await core.SaveChangesAsync();
            userId = user.Id;
        }

        var store = new UserRoleStore(coreFactory, inspectorFactory);

        // Пусто — режим первичной настройки открыт (иначе назначить первого Администратора некому).
        (await store.AnyAdministratorAsync()).ShouldBeFalse();
        (await store.GetRoleAsync(userId)).ShouldBeNull();

        var listed = await store.ListAsync();
        listed.ShouldHaveSingleItem().Role.ShouldBeNull();

        // КЛЮЧЕВОЙ СЛУЧАЙ (из-за него первая редакция фикса запирала систему): назначена роль, но
        // НЕ администраторская. Реестр уже не пуст — однако Администратора нет, значит окно ОБЯЗАНО
        // остаться открытым, иначе управление ролями теряется навсегда.
        await store.SetRoleAsync(userId, UserRole.Manager);
        (await store.GetRoleAsync(userId)).ShouldBe(UserRole.Manager);
        (await store.AnyAdministratorAsync()).ShouldBeFalse();

        // Появился Администратор — окно закрывается.
        await store.SetRoleAsync(userId, UserRole.Administrator);
        (await store.AnyAdministratorAsync()).ShouldBeTrue();

        // Смена роли не создаёт вторую строку (один пользователь — одна роль, уникальный индекс).
        (await store.ListAsync()).ShouldHaveSingleItem().Role.ShouldBe(UserRole.Administrator);

        // Разжалование последнего Администратора ОСОЗНАННО открывает окно снова: система не должна
        // запираться насмерть (иначе восстановление — только правкой БД руками).
        await store.SetRoleAsync(userId, UserRole.Inspector);
        (await store.AnyAdministratorAsync()).ShouldBeFalse();

        // Полное снятие роли — тоже открытое окно.
        await store.SetRoleAsync(userId, null);
        (await store.GetRoleAsync(userId)).ShouldBeNull();
        (await store.AnyAdministratorAsync()).ShouldBeFalse();
    }
}
