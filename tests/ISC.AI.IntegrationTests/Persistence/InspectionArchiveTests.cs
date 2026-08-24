using System;
using System.Linq;
using System.Threading.Tasks;
using ISC.AI.Profile.Inspector.Data;
using ISC.AI.Profile.Inspector.Domain.Entities;
using ISC.AI.Profile.Inspector.Domain.Enums;
using ISC.AI.Profile.Inspector.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Testcontainers.PostgreSql;
using Xunit;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Архив проверок (Э5, §5.2.4) на настоящем PostgreSQL. Требуется Docker.
/// </summary>
/// <remarks>
/// Ключевые инварианты: группа — пара «справка × подразделение» (одна справка в двух подразделениях —
/// две группы); нарушения без справки не теряются (группа «вне проверок»); отбор применяется
/// И к заголовкам групп, И к их строкам — иначе сводка разошлась бы с содержимым; счёт групп — до среза.
/// </remarks>
public sealed class InspectionArchiveTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = TestPostgres.Create();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "Группировка: справка × подразделение, «вне проверок» отдельно, свежие первыми")]
    public async Task Groups_by_reference_and_division_with_orphans()
    {
        var (store, ids) = await BuildAsync();
        await SeedScenarioAsync(ids);

        var page = await store.ListAsync(new ArchiveFilter());

        page.TotalGroups.ShouldBe(3);
        // Свежие проверки первыми — по дате последнего нарушения группы.
        page.Groups[0].ReferenceDocRef.ShouldBeNull();                 // вне проверок, 25.05
        page.Groups[0].DivisionName.ShouldBe("Альфа");
        page.Groups[1].ReferenceDocRef.ShouldBe("СП-1");               // Альфа, 20.05
        page.Groups[1].DivisionName.ShouldBe("Альфа");
        page.Groups[2].ReferenceDocRef.ShouldBe("СП-1");               // Бета, 15.05 — ОТДЕЛЬНАЯ группа
        page.Groups[2].DivisionName.ShouldBe("Бета");

        var inspection = page.Groups[1];
        inspection.ViolationCount.ShouldBe(2);
        inspection.OpenCount.ShouldBe(1);
        inspection.FirstDetectedAt.ShouldBe(new DateOnly(2026, 5, 10));
        inspection.LastDetectedAt.ShouldBe(new DateOnly(2026, 5, 20));
        inspection.MaxSeverity.ShouldBe(ViolationSeverity.High);
        inspection.Violations.Count.ShouldBe(2);
        inspection.Violations.Select(v => v.DetectedAt)
            .ShouldBe([new DateOnly(2026, 5, 20), new DateOnly(2026, 5, 10)]);
    }

    [Fact(DisplayName = "Отбор влияет и на заголовки групп, и на строки; поиск по номеру экранирует LIKE")]
    public async Task Filters_shape_both_heads_and_rows()
    {
        var (store, ids) = await BuildAsync();
        await SeedScenarioAsync(ids);

        // По тяжести High остаётся одна группа, и в её сводке — только отобранное нарушение.
        var high = await store.ListAsync(new ArchiveFilter(Severity: ViolationSeverity.High));
        high.TotalGroups.ShouldBe(1);
        high.Groups[0].ReferenceDocRef.ShouldBe("СП-1");
        high.Groups[0].ViolationCount.ShouldBe(1);
        high.Groups[0].Violations.ShouldHaveSingleItem().Severity.ShouldBe(ViolationSeverity.High);

        // Поиск по номеру (регистронезависимый) — группа «вне проверок» не попадает.
        var byRef = await store.ListAsync(new ArchiveFilter(RefSearch: "сп-1"));
        byRef.TotalGroups.ShouldBe(2);
        byRef.Groups.ShouldAllBe(g => g.ReferenceDocRef == "СП-1");

        // Подразделение + счёт до среза страницы.
        var alpha = await store.ListAsync(new ArchiveFilter(DivisionId: ids.DivisionA, PageSize: 1));
        alpha.TotalGroups.ShouldBe(2);
        alpha.Groups.Count.ShouldBe(1);
    }

    private async Task SeedScenarioAsync(TestIds ids)
    {
        // Справка СП-1 в «Альфе»: два нарушения (High/просрочено и Medium/устранено);
        // та же СП-1 в «Бете»: одно; и одно нарушение «Альфы» вовсе без справки.
        await SeedViolationAsync(ids.DivisionA, ids.Kind, ViolationSeverity.High,
            new DateOnly(2026, 5, 10), RemediationStatus.Overdue, "СП-1");
        await SeedViolationAsync(ids.DivisionA, ids.Kind, ViolationSeverity.Medium,
            new DateOnly(2026, 5, 20), RemediationStatus.Resolved, "СП-1");
        await SeedViolationAsync(ids.DivisionB, ids.Kind, ViolationSeverity.Low,
            new DateOnly(2026, 5, 15), RemediationStatus.Resolved, "СП-1");
        await SeedViolationAsync(ids.DivisionA, ids.Kind, ViolationSeverity.Critical,
            new DateOnly(2026, 5, 25), RemediationStatus.UnderControl, null);
    }

    private async Task<(InspectionArchiveStore Store, TestIds Ids)> BuildAsync()
    {
        await using (var db = new CoreContextFactory(_postgres.GetConnectionString()).CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        var inspector = new InspectorContextFactory(_postgres.GetConnectionString());
        await using (var db = inspector.CreateDbContext())
        {
            await db.Database.MigrateAsync();

            var divisionA = new Division { Name = "Альфа" };
            var divisionB = new Division { Name = "Бета" };
            var sphere = new ViolationCategory { Name = "Документооборот" };
            db.Divisions.AddRange(divisionA, divisionB);
            db.ViolationCategories.Add(sphere);
            await db.SaveChangesAsync();

            var kind = new ViolationCategory { Name = "Просрочка регистрации", ParentId = sphere.Id };
            db.ViolationCategories.Add(kind);
            await db.SaveChangesAsync();

            return (new InspectionArchiveStore(inspector), new TestIds(divisionA.Id, divisionB.Id, kind.Id));
        }
    }

    private async Task SeedViolationAsync(
        int divisionId, int categoryId, ViolationSeverity severity, DateOnly detectedAt,
        RemediationStatus status, string? referenceDocRef)
    {
        var factory = new InspectorContextFactory(_postgres.GetConnectionString());
        await using var db = factory.CreateDbContext();
        db.Violations.Add(new Violation
        {
            DivisionId = divisionId,
            CategoryId = categoryId,
            Severity = severity,
            DetectedAt = detectedAt,
            RemediationStatus = status,
            ReferenceDocRef = referenceDocRef,
        });
        await db.SaveChangesAsync();
    }

    private sealed record TestIds(int DivisionA, int DivisionB, int Kind);
}
