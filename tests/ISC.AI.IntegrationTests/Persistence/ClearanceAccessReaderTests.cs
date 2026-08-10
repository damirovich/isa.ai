using ISC.AI.Persistence;
using ISC.AI.Persistence.Entities;
using ISC.AI.Persistence.Security;
using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;
using Shouldly;
using Testcontainers.PostgreSql;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Э3-08 (ТБ-012/016/021): допуск читается из БД на каждую операцию — отзыв действует немедленно;
/// без действующего допуска контекст доступа не выдаётся (fail-closed). Реальный PostgreSQL
/// через Testcontainers (критерий приёмки Э3-08 «отзыв допуска действует немедленно»).
/// </summary>
/// <remarks>Требуется Docker.</remarks>
[Trait("Category", "Gate")]
public sealed class ClearanceAccessReaderTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("pgvector/pgvector:pg16").Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "Допуск сворачивается в AccessContext; отзыв и деактивация действуют немедленно")]
    public async Task Clearance_maps_to_access_context_and_revocation_is_immediate()
    {
        var factory = new CoreContextFactory(_postgres.GetConnectionString());
        int userId;
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
            var user = new AppUserEntity
            {
                UserName = "inspector1",
                ExternalId = "ext-1",
                Clearance = new ClearanceEntity { MaxClassification = 2, DivisionScope = [7, 8] },
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            userId = user.Id;
        }

        var reader = new ClearanceAccessReader(factory);

        // Действующий допуск: потолок грифа и подразделения — из core.clearance.
        var access = await reader.ReadAsync(userId);
        access.ShouldNotBeNull();
        access.SubjectId.ShouldBe(userId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        access.MaxClassification.ShouldBe<short>(2);
        access.AllowedDivisions.ShouldBe([7, 8]);

        // ОТЗЫВ допуска (мягкое удаление записи) — следующая же операция без допуска (ТБ-016).
        await using (var db = factory.CreateDbContext())
        {
            var clearance = await db.Clearances.SingleAsync(c => c.UserId == userId);
            db.Clearances.Remove(clearance); // AuditedDbContext превращает в IsDeleted=true
            await db.SaveChangesAsync();
        }

        (await reader.ReadAsync(userId)).ShouldBeNull();

        // Новый допуск выдан, но пользователь ДЕАКТИВИРОВАН — доступа тоже нет (ТБ-016, локальная блокировка).
        await using (var db = factory.CreateDbContext())
        {
            db.Clearances.Add(new ClearanceEntity { UserId = userId, MaxClassification = 1, DivisionScope = [7] });
            var user = await db.Users.SingleAsync(u => u.Id == userId);
            user.IsActive = false;
            await db.SaveChangesAsync();
        }

        (await reader.ReadAsync(userId)).ShouldBeNull();

        // Неизвестный пользователь — fail-closed.
        (await reader.ReadAsync(999_999)).ShouldBeNull();
    }
}
