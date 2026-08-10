using ISC.AI.Modules.DocFlow.Domain.Services;
using ISC.AI.Persistence;
using ISC.AI.Persistence.Entities;
using ISC.AI.Profile.Inspector.Data;
using ISC.AI.Profile.Inspector.Domain.Entities;
using ISC.AI.Profile.Inspector.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;
using Shouldly;
using Testcontainers.PostgreSql;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Справочник подразделений (§4.2): счётчики использования, вывод из обращения, удаление.
/// Требуется Docker.
/// </summary>
/// <remarks>
/// Ключевой инвариант: удалить можно ТОЛЬКО то подразделение, за которым ничего не числится.
/// Ссылки на подразделение — по значению, без FK через границы схем (ТО-инф-06), поэтому база
/// удаление не остановит: висячий номер молча останется в допусках и документах, и решётка доступа
/// начнёт пропускать в никуда. Здесь эту проверку и держим.
/// </remarks>
public sealed class DivisionAdminTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("pgvector/pgvector:pg16").Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "Пустое подразделение удаляется; с людьми, потомками или поручениями — нет")]
    public async Task Division_is_deletable_only_while_unused()
    {
        var store = await BuildAsync(new StubUsage());

        var free = await store.CreateAsync("Ошибочное", "ERR", null);
        var withUsers = await store.CreateAsync("С людьми", "U", null);
        var parent = await store.CreateAsync("Родитель", "P", null);
        await store.CreateAsync("Потомок", "C", parent);

        await GiveClearanceAsync(withUsers);

        var list = await store.ListAsync();
        list.Single(d => d.Id == free).CanDelete.ShouldBeTrue();
        list.Single(d => d.Id == withUsers).Users.ShouldBe(1);
        list.Single(d => d.Id == withUsers).CanDelete.ShouldBeFalse();
        list.Single(d => d.Id == parent).Children.ShouldBe(1);
        list.Single(d => d.Id == parent).CanDelete.ShouldBeFalse();

        (await store.DeleteAsync(withUsers)).ShouldBe(DivisionWriteResult.InUse);
        (await store.DeleteAsync(parent)).ShouldBe(DivisionWriteResult.InUse);
        (await store.DeleteAsync(free)).ShouldBe(DivisionWriteResult.Ok);
        (await store.DeleteAsync(free)).ShouldBe(DivisionWriteResult.NotFound);
    }

    [Fact(DisplayName = "Подразделение с документами и поручениями удалить нельзя, счётчики видны")]
    public async Task Division_used_by_docflow_is_protected()
    {
        // Модуль отвечает, что на подразделении 1 есть документ и два поручения.
        var store = await BuildAsync(new StubUsage(new Dictionary<int, DivisionUsage>()));

        var id = await store.CreateAsync("С документами", "D", null);

        var usage = new StubUsage(new Dictionary<int, DivisionUsage> { [id] = new(1, 2) });
        var storeWithUsage = await BuildAsync(usage, migrate: false);

        var node = (await storeWithUsage.ListAsync()).Single(d => d.Id == id);
        node.Documents.ShouldBe(1);
        node.Assignments.ShouldBe(2);
        node.CanDelete.ShouldBeFalse();

        (await storeWithUsage.DeleteAsync(id)).ShouldBe(DivisionWriteResult.InUse);
    }

    /// <summary>
    /// Расформированное подразделение удалить нельзя — за ним история. Признак активности выводит
    /// его из обращения, сохраняя её.
    /// </summary>
    [Fact(DisplayName = "Вывод из обращения не удаляет подразделение и не трогает историю")]
    public async Task Deactivation_keeps_the_division()
    {
        var store = await BuildAsync(new StubUsage());
        var id = await store.CreateAsync("Расформированное", "OLD", null);

        (await store.SetActiveAsync(id, isActive: false)).ShouldBeTrue();

        var node = (await store.ListAsync()).Single(d => d.Id == id);
        node.IsActive.ShouldBeFalse();

        // В справочнике осталось, вернуть в обращение можно.
        (await store.SetActiveAsync(id, isActive: true)).ShouldBeTrue();
        (await store.ListAsync()).Single(d => d.Id == id).IsActive.ShouldBeTrue();
    }

    private async Task GiveClearanceAsync(int divisionId)
    {
        var factory = new CoreContextFactory(_postgres.GetConnectionString());
        await using var db = factory.CreateDbContext();

        var user = new AppUserEntity { UserName = $"user{divisionId}", DisplayName = "Пользователь" };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        db.Clearances.Add(new ClearanceEntity
        {
            UserId = user.Id,
            MaxClassification = 1,
            DivisionScope = [divisionId],
        });
        await db.SaveChangesAsync();
    }

    private async Task<DivisionAdminStore> BuildAsync(IDivisionUsage usage, bool migrate = true)
    {
        var inspector = new InspectorContextFactory(_postgres.GetConnectionString());
        var core = new CoreContextFactory(_postgres.GetConnectionString());

        if (migrate)
        {
            await using (var db = core.CreateDbContext())
            {
                await db.Database.MigrateAsync();
            }

            await using (var db = inspector.CreateDbContext())
            {
                await db.Database.MigrateAsync();
            }
        }

        return new DivisionAdminStore(inspector, core, usage);
    }

    /// <summary>Заглушка ответа модуля: настоящий запрос идёт в другую схему и здесь не нужен.</summary>
    private sealed class StubUsage(IReadOnlyDictionary<int, DivisionUsage>? counts = null) : IDivisionUsage
    {
        public Task<IReadOnlyDictionary<int, DivisionUsage>> CountAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(counts ?? new Dictionary<int, DivisionUsage>());
    }
}
