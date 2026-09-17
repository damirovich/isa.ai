using System.Linq;
using System.Threading.Tasks;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Persistence.Entities;
using ISC.AI.Profile.Investigation.Data;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Testcontainers.PostgreSql;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Роли профиля «Следствие» (ТП-004): реестр поверх двух схем (<c>core.app_user</c> +
/// <c>investigation.user_role_assignment</c>), права портов «Медиа» по роли (<c>MediaAdministration</c>,
/// <c>VerificationPolicy</c>, ТБ-073), справочник подразделений с числом пользователей в допуске.
/// Реальный Postgres через Testcontainers; мигрируются обе схемы.
/// </summary>
public sealed class InvestigationRoleStoreTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = TestPostgres.Create();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "UserRoleStore: назначение/снятие роли, список активных пользователей с ролями, выборка по роли, признак Администратора")]
    public async Task Role_store_joins_core_users_with_profile_roles()
    {
        var (factory, core) = await MigrateBothAsync();

        int alice, bob, carol;
        await using (var db = core.CreateDbContext())
        {
            var a = new AppUserEntity { UserName = "alice", DisplayName = "Алиева А." };
            var b = new AppUserEntity { UserName = "bob", DisplayName = "Борисов Б." };
            var c = new AppUserEntity { UserName = "carol", DisplayName = "Уволенная", IsActive = false };
            db.Users.AddRange(a, b, c);
            await db.SaveChangesAsync();
            (alice, bob, carol) = (a.Id, b.Id, c.Id);
        }

        var roles = new UserRoleStore(core, factory);

        (await roles.AnyAdministratorAsync()).ShouldBeFalse();
        (await roles.GetRoleAsync(alice)).ShouldBeNull();

        await roles.SetRoleAsync(alice, InvestigationRole.Investigator);
        await roles.SetRoleAsync(bob, InvestigationRole.Administrator);
        await roles.SetRoleAsync(carol, InvestigationRole.Investigator);

        (await roles.GetRoleAsync(alice)).ShouldBe(InvestigationRole.Investigator);
        (await roles.AnyAdministratorAsync()).ShouldBeTrue();
        (await roles.ListUserIdsByRoleAsync(InvestigationRole.Investigator)).OrderBy(id => id).ShouldBe(new[] { alice, carol }.OrderBy(id => id));

        // Список — только активные пользователи ядра, по имени, с ролью или без.
        var list = await roles.ListAsync();
        list.Select(r => r.UserId).ShouldBe([alice, bob]);
        list.Single(r => r.UserId == alice).Role.ShouldBe(InvestigationRole.Investigator);
        list.Single(r => r.UserId == bob).DisplayName.ShouldBe("Борисов Б.");

        // Смена и снятие роли — та же запись (unique user_id), не вторая.
        await roles.SetRoleAsync(alice, InvestigationRole.Head);
        (await roles.GetRoleAsync(alice)).ShouldBe(InvestigationRole.Head);
        await roles.SetRoleAsync(alice, null);
        (await roles.GetRoleAsync(alice)).ShouldBeNull();
        await using (var db = factory.CreateDbContext())
        {
            (await db.UserRoleAssignments.CountAsync(r => r.UserId == alice)).ShouldBe(0);
        }
    }

    [Fact(DisplayName = "Порты «Медиа» по роли: загрузка/поиск/удаление — по ТП-004; Администратор не эксперт и не верификатор (ТБ-073); без роли — ничего")]
    public async Task Media_ports_answer_by_role_without_bootstrap_mode()
    {
        var (factory, core) = await MigrateBothAsync();
        await InvestigationTestKit.AssignRolesAsync(factory,
            (10, InvestigationRole.Investigator),
            (20, InvestigationRole.Head),
            (30, InvestigationRole.Administrator),
            (40, InvestigationRole.FaceExpert),
            (41, InvestigationRole.Verifier),
            (60, InvestigationRole.SecurityOfficer));

        var roles = new UserRoleStore(core, factory);

        static MediaAdministration For(UserRoleStore roles, int? userId) => new(roles, new FixedSubjectProvider(userId));

        (await For(roles, 10).CanUploadAsync()).ShouldBeTrue();
        (await For(roles, 10).CanSearchAsync()).ShouldBeTrue();
        (await For(roles, 10).CanPurgeAsync()).ShouldBeFalse();

        (await For(roles, 40).CanUploadAsync()).ShouldBeFalse();
        (await For(roles, 40).CanSearchAsync()).ShouldBeTrue();
        (await For(roles, 40).CanPurgeAsync()).ShouldBeFalse();

        (await For(roles, 20).CanPurgeAsync()).ShouldBeTrue();
        (await For(roles, 30).CanPurgeAsync()).ShouldBeTrue();
        (await For(roles, 41).CanSearchAsync()).ShouldBeFalse();
        (await For(roles, 60).CanUploadAsync()).ShouldBeFalse();

        // Без роли и без субъекта — отказ; «пока Администратора нет — можно всем» здесь НЕ действует.
        (await For(roles, 50).CanSearchAsync()).ShouldBeFalse();
        (await For(roles, null).CanUploadAsync()).ShouldBeFalse();
        await using (var db = factory.CreateDbContext())
        {
            db.UserRoleAssignments.RemoveRange(db.UserRoleAssignments.Where(r => r.Role == InvestigationRole.Administrator));
            await db.SaveChangesAsync();
        }

        (await roles.AnyAdministratorAsync()).ShouldBeFalse();
        (await For(roles, 50).CanUploadAsync()).ShouldBeFalse();

        var policy = new VerificationPolicy(roles);
        (await policy.CanActAsync(VerificationStage.Expert, 40)).ShouldBeTrue();
        (await policy.CanActAsync(VerificationStage.Verifier, 40)).ShouldBeFalse();
        (await policy.CanActAsync(VerificationStage.Expert, 41)).ShouldBeFalse();
        (await policy.CanActAsync(VerificationStage.Verifier, 41)).ShouldBeTrue();
        (await policy.CanActAsync(VerificationStage.Expert, 20)).ShouldBeFalse();
        (await policy.CanActAsync(VerificationStage.Verifier, 10)).ShouldBeFalse();
        (await policy.CanActAsync(VerificationStage.Expert, 50)).ShouldBeFalse();
    }

    [Fact(DisplayName = "Справочник подразделений: число пользователей в допуске из core.clearance; для докфлоу — только действующие, «Родитель / Дочернее»")]
    public async Task Division_stores_count_clearances_and_expose_active_hierarchy()
    {
        var (factory, core) = await MigrateBothAsync();

        var divisions = new DivisionAdminStore(factory, core);
        var root = await divisions.CreateAsync("Главное управление", "ГУ", parentId: null);
        var child = await divisions.CreateAsync("Отдел № 1", null, parentId: root);
        var retired = await divisions.CreateAsync("Расформированный", "Р", parentId: root);
        (await divisions.SetActiveAsync(retired, false)).ShouldBe(DivisionWriteResult.Ok);
        (await divisions.SetActiveAsync(999_999, false)).ShouldBe(DivisionWriteResult.NotFound);
        (await divisions.RenameAsync(child, "Отдел № 1 (следственный)", "О1")).ShouldBe(DivisionWriteResult.Ok);

        await using (var db = core.CreateDbContext())
        {
            var u1 = new AppUserEntity { UserName = "u1" };
            var u2 = new AppUserEntity { UserName = "u2" };
            db.Users.AddRange(u1, u2);
            await db.SaveChangesAsync();
            db.Clearances.AddRange(
                new ClearanceEntity { UserId = u1.Id, MaxClassification = 2, DivisionScope = [root, child] },
                new ClearanceEntity { UserId = u2.Id, MaxClassification = 1, DivisionScope = [child] });
            await db.SaveChangesAsync();
        }

        var nodes = await divisions.ListAsync();
        nodes.Count.ShouldBe(3);
        nodes.Single(n => n.Id == root).Users.ShouldBe(1);
        nodes.Single(n => n.Id == child).Users.ShouldBe(2);
        nodes.Single(n => n.Id == child).Code.ShouldBe("О1");
        nodes.Single(n => n.Id == retired).IsActive.ShouldBeFalse();
        (await divisions.ExistsActiveAsync(child)).ShouldBeTrue();
        (await divisions.ExistsActiveAsync(retired)).ShouldBeFalse();

        var directory = new InvestigationDivisionDirectory(factory);
        var items = await directory.ListAsync();
        items.Select(i => i.Name).ShouldBe(["Главное управление", "Главное управление / Отдел № 1 (следственный)"]);
    }

    private async Task<(InvestigationContextFactory Factory, CoreContextFactory Core)> MigrateBothAsync()
    {
        var factory = new InvestigationContextFactory(_postgres.GetConnectionString());
        var core = new CoreContextFactory(_postgres.GetConnectionString());
        await using (var db = core.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        await InvestigationTestKit.MigrateAsync(factory);
        return (factory, core);
    }
}
