using System.Linq;
using System.Threading.Tasks;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using Shouldly;
using Testcontainers.PostgreSql;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Решётка на делах профиля «Следствие» (ТБ-020/021/024/071, ТФ-ДЕЛ-03): floor ядра (гриф/подразделение)
/// и правило <c>CaseAccessRule</c> (роль/владение) применяются на стороне БД через <c>CaseStore</c>.
/// Реальный Postgres через Testcontainers.
/// </summary>
[Trait("Category", "Gate")]
public sealed class InvestigationCaseAccessTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = TestPostgres.Create();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "Дела вне допуска не выдаются; следователь — только свои; руководитель — подразделения; без роли — пусто")]
    public async Task Case_lattice_enforces_clearance_and_role_on_db_side()
    {
        var factory = new InvestigationContextFactory(_postgres.GetConnectionString());
        var core = new CoreContextFactory(_postgres.GetConnectionString());
        await InvestigationTestKit.MigrateAsync(factory);

        // 10, 11 — Следователи; 20 — Руководитель; 30 — Администратор; 50 — без роли.
        await InvestigationTestKit.AssignRolesAsync(factory,
            (10, InvestigationRole.Investigator),
            (11, InvestigationRole.Investigator),
            (20, InvestigationRole.Head),
            (30, InvestigationRole.Administrator));

        var store = InvestigationTestKit.CreateCaseStore(factory, core);
        var admin = InvestigationTestKit.Access(30, 9, 5, 6);

        // A: подразделение 5, гриф 1, следователь 10. B: подразделение 5, гриф 3, следователь 11.
        // C: подразделение 6, гриф 1, следователь 10.
        var a = await store.CreateAsync(InvestigationTestKit.Draft("A-1", 5, 1, 10), admin);
        var b = await store.CreateAsync(InvestigationTestKit.Draft("B-1", 5, 3, 11), admin);
        var c = await store.CreateAsync(InvestigationTestKit.Draft("C-1", 6, 1, 10), admin);
        a.Result.ShouldBe(CaseWriteResult.Ok);
        b.Result.ShouldBe(CaseWriteResult.Ok);
        c.Result.ShouldBe(CaseWriteResult.Ok);

        // Следователь 10 с допуском гриф ≤ 2, подразделение 5: B выше допуска, C — чужое подразделение.
        var investigator10 = InvestigationTestKit.Access(10, 2, 5);
        (await store.ListAsync(new CaseFilter(), investigator10)).Rows.Select(r => r.Number).ShouldBe(["A-1"]);
        (await store.ListAccessibleIdsAsync(investigator10)).ShouldBe([a.CaseId]);

        // Тот же следователь с широким допуском — всё равно только СВОИ дела (A и C), не B.
        var investigator10Wide = InvestigationTestKit.Access(10, 9, 5, 6);
        (await store.ListAsync(new CaseFilter(), investigator10Wide)).Rows.Select(r => r.Number)
            .OrderBy(n => n).ShouldBe(["A-1", "C-1"]);

        // Следователь 11 — только B.
        (await store.ListAsync(new CaseFilter(), InvestigationTestKit.Access(11, 9, 5, 6))).Rows
            .Select(r => r.Number).ShouldBe(["B-1"]);

        // Руководитель 20 с допуском на подразделение 5 — все дела подразделения (A, B), не C.
        (await store.ListAsync(new CaseFilter(), InvestigationTestKit.Access(20, 9, 5))).Rows
            .Select(r => r.Number).OrderBy(n => n).ShouldBe(["A-1", "B-1"]);

        // Руководитель с допуском на оба подразделения — все три.
        (await store.ListAsync(new CaseFilter(), InvestigationTestKit.Access(20, 9, 5, 6))).TotalCount.ShouldBe(3);

        // Без роли (50) — default-deny даже с максимальным допуском (ТБ-012).
        (await store.ListAsync(new CaseFilter(), InvestigationTestKit.Access(50, 9, 5, 6))).Rows.ShouldBeEmpty();

        // Карточка: недоступное дело неотличимо от несуществующего (ТБ-021).
        (await store.GetAsync(b.CaseId, investigator10Wide)).ShouldBeNull();
        (await store.GetAsync(a.CaseId, investigator10)).ShouldNotBeNull().Number.ShouldBe("A-1");

        // Нечисловой субъект (dev-заглушка) — роли нет → пусто.
        (await store.ListAsync(new CaseFilter(), new ISC.AI.Abstractions.Security.AccessContext("dev", 9, [5, 6]))).Rows.ShouldBeEmpty();
    }

    [Fact(DisplayName = "CreateAsync: гриф выше допуска или чужое подразделение → OutsideClearance (ТБ-024); дубль номера → DuplicateNumber")]
    public async Task Create_rejects_outside_clearance_and_duplicate_number()
    {
        var factory = new InvestigationContextFactory(_postgres.GetConnectionString());
        var core = new CoreContextFactory(_postgres.GetConnectionString());
        await InvestigationTestKit.MigrateAsync(factory);
        await InvestigationTestKit.AssignRolesAsync(factory, (10, InvestigationRole.Investigator));

        var store = InvestigationTestKit.CreateCaseStore(factory, core);
        var investigator = InvestigationTestKit.Access(10, 2, 5);

        (await store.CreateAsync(InvestigationTestKit.Draft("X-1", 5, 3, 10), investigator)).Result
            .ShouldBe(CaseWriteResult.OutsideClearance);
        (await store.CreateAsync(InvestigationTestKit.Draft("X-1", 6, 1, 10), investigator)).Result
            .ShouldBe(CaseWriteResult.OutsideClearance);

        var first = await store.CreateAsync(InvestigationTestKit.Draft("X-1", 5, 2, 10), investigator);
        first.Result.ShouldBe(CaseWriteResult.Ok);

        // Тот же номер в том же подразделении — дубль; в другом подразделении — допустимо.
        (await store.CreateAsync(InvestigationTestKit.Draft("X-1", 5, 1, 10), investigator)).Result
            .ShouldBe(CaseWriteResult.DuplicateNumber);

        var wide = InvestigationTestKit.Access(10, 2, 5, 6);
        (await store.CreateAsync(InvestigationTestKit.Draft("X-1", 6, 1, 10), wide)).Result
            .ShouldBe(CaseWriteResult.Ok);

        // Создатель без указанного следователя видит дело как автор записи (CreatedByUserId).
        var own = await store.CreateAsync(InvestigationTestKit.Draft("Y-1", 5, 1, investigatorUserId: null), investigator);
        own.Result.ShouldBe(CaseWriteResult.Ok);
        (await store.GetAsync(own.CaseId, investigator)).ShouldNotBeNull();
    }

    [Fact(DisplayName = "Запись по невидимому делу → NotFound; смена статуса «Закрыто» ставит ClosedAt; основание добавляется только в доступное дело")]
    public async Task Write_paths_follow_the_same_lattice()
    {
        var factory = new InvestigationContextFactory(_postgres.GetConnectionString());
        var core = new CoreContextFactory(_postgres.GetConnectionString());
        await InvestigationTestKit.MigrateAsync(factory);
        await InvestigationTestKit.AssignRolesAsync(factory,
            (10, InvestigationRole.Investigator),
            (11, InvestigationRole.Investigator),
            (20, InvestigationRole.Head));

        var store = InvestigationTestKit.CreateCaseStore(factory, core);
        var head = InvestigationTestKit.Access(20, 9, 5);
        var created = await store.CreateAsync(InvestigationTestKit.Draft("W-1", 5, 1, 10), head);
        created.Result.ShouldBe(CaseWriteResult.Ok);

        var stranger = InvestigationTestKit.Access(11, 9, 5);
        var owner = InvestigationTestKit.Access(10, 9, 5);

        (await store.UpdateAsync(created.CaseId, "Новое название", CaseKind.Material, new DateOnly(2026, 9, 2), 10, "основание", stranger))
            .ShouldBe(CaseWriteResult.NotFound);
        (await store.SetStatusAsync(created.CaseId, CaseStatus.Closed, stranger)).ShouldBe(CaseWriteResult.NotFound);
        (await store.AddAuthorizationAsync(
            new SearchAuthorizationDraft(created.CaseId, AuthorizationKind.Resolution, "№ 1", new DateOnly(2026, 9, 3), null, null, null),
            stranger)).Result.ShouldBe(CaseWriteResult.NotFound);

        (await store.UpdateAsync(created.CaseId, "Новое название", CaseKind.Material, new DateOnly(2026, 9, 2), 10, "основание", owner))
            .ShouldBe(CaseWriteResult.Ok);
        (await store.AddAuthorizationAsync(
            new SearchAuthorizationDraft(created.CaseId, AuthorizationKind.Resolution, "№ 1", new DateOnly(2026, 9, 3), null, null, null),
            owner)).Result.ShouldBe(CaseWriteResult.Ok);
        (await store.SetStatusAsync(created.CaseId, CaseStatus.Closed, owner)).ShouldBe(CaseWriteResult.Ok);

        var details = (await store.GetAsync(created.CaseId, owner)).ShouldNotBeNull();
        details.Title.ShouldBe("Новое название");
        details.Kind.ShouldBe(CaseKind.Material);
        details.Status.ShouldBe(CaseStatus.Closed);
        details.ClosedAt.ShouldNotBeNull();
        details.Authorizations.ShouldHaveSingleItem().Reference.ShouldBe("№ 1");

        // Повторное открытие снимает ClosedAt.
        (await store.SetStatusAsync(created.CaseId, CaseStatus.InProgress, owner)).ShouldBe(CaseWriteResult.Ok);
        (await store.GetAsync(created.CaseId, owner)).ShouldNotBeNull().ClosedAt.ShouldBeNull();
    }
}
