using System;
using System.Linq;
using System.Threading.Tasks;
using ISC.AI.Profile.Investigation.Data;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;
using Testcontainers.PostgreSql;
using Xunit;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Отдел ОН/ОУ на настоящей БД (ТЭ-008, ADR-0039): отметка хранится у подразделения, группы наследуют её от отдела;
/// дело получает отдел по своему подразделению в списке и карточке; отбор по отделу сужает список; смена отметки
/// переносит дела в другой отдел; база не принимает других значений. Отбор не открывает чужого — доступ решает
/// допуск по подразделениям.
/// </summary>
public sealed class InvestigationCaseDirectionTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = TestPostgres.Create();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "Отдел — по подразделению: своя отметка и унаследованная; отбор по отделу; смена отметки переносит дела; допуск не расширяется")]
    public async Task Department_comes_from_division()
    {
        var factory = new InvestigationContextFactory(_postgres.GetConnectionString());
        var core = new CoreContextFactory(_postgres.GetConnectionString());
        await using (var coreDb = core.CreateDbContext())
        {
            await coreDb.Database.MigrateAsync();
        }

        await InvestigationTestKit.MigrateAsync(factory);
        await InvestigationTestKit.AssignRolesAsync(factory, (10, InvestigationRole.Investigator), (20, InvestigationRole.Head));

        // ОПУ → Отдел ОН (ОН) → Группа ОН-1 (без своей отметки); ОПУ → Отдел ОУ (ОУ); ОПУ → Прочее (без отметки).
        var divisions = new DivisionAdminStore(factory, core);
        var root = await divisions.CreateAsync("ОПУ", null, parentId: null, direction: null);
        var on = await divisions.CreateAsync("Отдел ОН", "ОН", root, CaseDirection.Surveillance);
        var group = await divisions.CreateAsync("Группа ОН-1", null, on, direction: null);
        var ou = await divisions.CreateAsync("Отдел ОУ", "ОУ", root, CaseDirection.Establishment);
        var other = await divisions.CreateAsync("Прочее", null, root, direction: null);

        (await divisions.ListAsync()).Single(d => d.Id == on).Direction.ShouldBe(CaseDirection.Surveillance);
        (await divisions.ListAsync()).Single(d => d.Id == group).Direction.ShouldBeNull(); // своей нет — только наследуемая

        var store = InvestigationTestKit.CreateCaseStore(factory, core);
        var owner = InvestigationTestKit.Access(10, 9, on, group, ou, other);
        var onCase = await store.CreateAsync(InvestigationTestKit.Draft("ОН-1", on, 1, 10), owner);
        var groupCase = await store.CreateAsync(InvestigationTestKit.Draft("ОН-2", group, 1, 10), owner);
        await store.CreateAsync(InvestigationTestKit.Draft("ОУ-1", ou, 1, 10), owner);
        var otherCase = await store.CreateAsync(InvestigationTestKit.Draft("П-1", other, 1, 10), owner);

        async Task<string[]> Numbers(CaseFilter filter, ISC.AI.Abstractions.Security.AccessContext access) =>
            (await store.ListAsync(filter, access)).Rows.Select(r => r.Number).Order(StringComparer.Ordinal).ToArray();

        // Отдел дела в строке списка и в карточке — по подразделению, в том числе через вышестоящее.
        var rows = (await store.ListAsync(new CaseFilter(), owner)).Rows;
        rows.Single(r => r.Id == onCase.CaseId).Direction.ShouldBe(CaseDirection.Surveillance);
        rows.Single(r => r.Id == groupCase.CaseId).Direction.ShouldBe(CaseDirection.Surveillance);
        rows.Single(r => r.Id == otherCase.CaseId).Direction.ShouldBeNull();
        (await store.GetAsync(groupCase.CaseId, owner)).ShouldNotBeNull().Direction.ShouldBe(CaseDirection.Surveillance);

        (await Numbers(new CaseFilter(), owner)).ShouldBe(["ОН-1", "ОН-2", "ОУ-1", "П-1"]);
        (await Numbers(new CaseFilter(Direction: CaseDirection.Surveillance), owner)).ShouldBe(["ОН-1", "ОН-2"]);
        (await Numbers(new CaseFilter(Direction: CaseDirection.Establishment), owner)).ShouldBe(["ОУ-1"]);

        // Группу отнесли к ОУ своей отметкой — её дело переходит в ОУ без правки самого дела.
        (await divisions.RenameAsync(group, "Группа ОН-1", null, CaseDirection.Establishment)).ShouldBe(DivisionWriteResult.Ok);
        (await store.GetAsync(groupCase.CaseId, owner)).ShouldNotBeNull().Direction.ShouldBe(CaseDirection.Establishment);
        (await Numbers(new CaseFilter(Direction: CaseDirection.Establishment), owner)).ShouldBe(["ОН-2", "ОУ-1"]);

        // Отбор сужает, но не расширяет: руководитель с допуском только к «Отделу ОУ» не видит дел ОН ни при каком отборе.
        var ouHead = InvestigationTestKit.Access(20, 9, ou);
        (await Numbers(new CaseFilter(), ouHead)).ShouldBe(["ОУ-1"]);
        (await Numbers(new CaseFilter(Direction: CaseDirection.Surveillance), ouHead)).ShouldBeEmpty();
        // Без роли — ничего (default-deny), отбор этого не меняет.
        (await Numbers(new CaseFilter(Direction: CaseDirection.Establishment), InvestigationTestKit.Access(99, 9, ou))).ShouldBeEmpty();

        // База не принимает отметок, кроме ОН (1) и ОУ (2).
        await using var db = factory.CreateDbContext();
        var violation = await Should.ThrowAsync<PostgresException>(() =>
            db.Database.ExecuteSqlInterpolatedAsync($"UPDATE investigation.division SET direction = 3 WHERE id = {on}"));
        violation.ConstraintName.ShouldBe("ck_division_direction");
    }
}
