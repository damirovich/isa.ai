using ISC.AI.Abstractions.Security;
using ISC.AI.Persistence;
using ISC.AI.Persistence.Entities;
using ISC.AI.Persistence.Security;
using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;
using Shouldly;
using Testcontainers.PostgreSql;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Ведение допусков из интерфейса (<see cref="IClearanceStore"/>, ТБ-011/016/021) на реальном
/// PostgreSQL: выдача, замена, отзыв и повторная выдача после отзыва. Требуется Docker.
/// </summary>
/// <remarks>
/// Цикл «отозвать → выдать заново» проверяется намеренно: уникальность <c>user_id</c> в таблице
/// допусков частичная (только среди живых записей), и без этого условия отозванный допуск навечно
/// занимал бы слот пользователя. Один раз этот дефект уже ловился на чтении — теперь он достижим
/// и с записи, из интерфейса.
/// </remarks>
public sealed class ClearanceStoreTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = TestPostgres.Create();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "Допуски: выдача, замена, отзыв, повторная выдача; отзыв виден чтению немедленно")]
    public async Task Clearance_lifecycle_end_to_end()
    {
        var factory = new CoreContextFactory(_postgres.GetConnectionString());
        int activeId, disabledId;
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
            var active = new AppUserEntity { UserName = "ivanov", DisplayName = "Иванов И.И." };
            var disabled = new AppUserEntity { UserName = "petrov", DisplayName = "Петров П.П.", IsActive = false };
            db.Users.AddRange(active, disabled);
            await db.SaveChangesAsync();
            (activeId, disabledId) = (active.Id, disabled.Id);
        }

        var store = new ClearanceStore(factory);
        var reader = new ClearanceAccessReader(factory);

        // До выдачи допуска субъект не имеет доступа НИ К ЧЕМУ (fail-closed, ТБ-021).
        (await reader.ReadAsync(activeId)).ShouldBeNull();

        var listedBefore = await store.ListAsync();
        listedBefore.Single(r => r.UserId == activeId).MaxClassification.ShouldBeNull();

        // Выдача: дубли и нули в списке подразделений отсеиваются, порядок нормализуется.
        (await store.SetAsync(activeId, 2, [9, 5, 5, 0, -1])).ShouldBeTrue();

        var granted = await reader.ReadAsync(activeId);
        granted.ShouldNotBeNull();
        granted.MaxClassification.ShouldBe((short)2);
        granted.AllowedDivisions.ShouldBe([5, 9], ignoreOrder: true);

        // Замена допуска — не вторая строка, а правка существующей.
        (await store.SetAsync(activeId, 0, [5])).ShouldBeTrue();
        var replaced = await reader.ReadAsync(activeId);
        replaced!.MaxClassification.ShouldBe((short)0);
        replaced.AllowedDivisions.ShouldBe([5]);

        // Пустой список подразделений — законное состояние «ни одного» (ТБ-021), а не «все».
        (await store.SetAsync(activeId, 0, [])).ShouldBeTrue();
        (await reader.ReadAsync(activeId))!.AllowedDivisions.ShouldBeEmpty();

        // Отзыв действует немедленно: допуск нигде не кэшируется (ТБ-016).
        (await store.RevokeAsync(activeId)).ShouldBeTrue();
        (await reader.ReadAsync(activeId)).ShouldBeNull();
        (await store.RevokeAsync(activeId)).ShouldBeFalse();

        // Штатный цикл ТБ-016: после отзыва допуск выдаётся заново (частичный уникальный индекс).
        (await store.SetAsync(activeId, 3, [5])).ShouldBeTrue();
        (await reader.ReadAsync(activeId))!.MaxClassification.ShouldBe((short)3);

        // Отключённому пользователю допуск не выдаётся: он всё равно не подействует (чтение требует
        // IsActive), а молчаливая запись создавала бы ложное впечатление выданного доступа.
        (await store.SetAsync(disabledId, 5, [5])).ShouldBeFalse();
        (await reader.ReadAsync(disabledId)).ShouldBeNull();
    }
}
