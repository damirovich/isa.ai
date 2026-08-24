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
/// Учёт нарушений и классификатор видов (Э5-01) на настоящем PostgreSQL. Требуется Docker.
/// </summary>
/// <remarks>
/// Ключевые инварианты: нарушение относится только к ВИДУ (нижний уровень классификатора) — сфера
/// целиком не категория факта; повторность — ПРОИЗВОДНЫЙ признак (не хранится и не разъезжается
/// с данными); отбор по сфере включает её виды; сид классификатора — только на пустом.
/// </remarks>
public sealed class ViolationRegistryTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = TestPostgres.Create();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "Нарушение принимает только вид нижнего уровня; сфера и битые ссылки — отказ")]
    public async Task Violation_requires_leaf_category_and_valid_references()
    {
        var (store, _, ids) = await BuildAsync();

        // Сфера целиком — не категория факта.
        var draft = Draft(ids.DivisionA, ids.Sphere);
        (await store.CreateAsync(draft)).Result.ShouldBe(ViolationWriteResult.CategoryNotLeaf);

        // Несуществующее подразделение / вид.
        (await store.CreateAsync(draft with { DivisionId = 999_999, CategoryId = ids.KindK1 }))
            .Result.ShouldBe(ViolationWriteResult.NotFound);
        (await store.CreateAsync(draft with { CategoryId = 999_999 }))
            .Result.ShouldBe(ViolationWriteResult.NotFound);

        // Вид нижнего уровня — принимается, карточка читается, правка меняет статус.
        var (result, violationId) = await store.CreateAsync(
            Draft(ids.DivisionA, ids.KindK1) with { Cause = "  причина  " });
        result.ShouldBe(ViolationWriteResult.Ok);

        var details = await store.GetAsync(violationId);
        details.ShouldNotBeNull();
        details.Cause.ShouldBe("причина"); // Trim при записи.

        (await store.UpdateAsync(violationId,
                Draft(ids.DivisionA, ids.KindK1) with { RemediationStatus = RemediationStatus.Resolved }))
            .ShouldBe(ViolationWriteResult.Ok);
        (await store.GetAsync(violationId))!.RemediationStatus.ShouldBe(RemediationStatus.Resolved);

        (await store.UpdateAsync(999_999, Draft(ids.DivisionA, ids.KindK1)))
            .ShouldBe(ViolationWriteResult.NotFound);
    }

    [Fact(DisplayName = "Отбор по сфере включает её виды; повторность производная; счёт — до среза страницы")]
    public async Task Listing_filters_by_sphere_and_derives_recurrence()
    {
        var (store, _, ids) = await BuildAsync();

        // Два факта одного вида в одном подразделении (повтор), один — того же вида в другом,
        // один — другого вида той же сферы.
        await store.CreateAsync(Draft(ids.DivisionA, ids.KindK1, new DateOnly(2026, 5, 10)));
        await store.CreateAsync(Draft(ids.DivisionA, ids.KindK1, new DateOnly(2026, 5, 20)));
        await store.CreateAsync(Draft(ids.DivisionB, ids.KindK1, new DateOnly(2026, 5, 21)));
        await store.CreateAsync(Draft(ids.DivisionA, ids.KindK2, new DateOnly(2026, 5, 22)));

        // Сфера включает оба вида; счёт — ДО среза страницы.
        var bySphere = await store.ListAsync(new ViolationListFilter(CategoryId: ids.Sphere, PageSize: 2));
        bySphere.TotalCount.ShouldBe(4);
        bySphere.Rows.Count.ShouldBe(2);

        var byKind = await store.ListAsync(new ViolationListFilter(CategoryId: ids.KindK1));
        byKind.TotalCount.ShouldBe(3);

        // Повторность: только пара одного вида в ОДНОМ подразделении; названия — из справочников.
        var all = await store.ListAsync(new ViolationListFilter());
        all.Rows.Where(r => r.DivisionId == ids.DivisionA && r.CategoryId == ids.KindK1)
            .ShouldAllBe(r => r.IsRecurring);
        all.Rows.Single(r => r.DivisionId == ids.DivisionB).IsRecurring.ShouldBeFalse();
        all.Rows.Single(r => r.CategoryId == ids.KindK2).IsRecurring.ShouldBeFalse();
        all.Rows.First().CategoryParentName.ShouldBe("Документооборот");

        // Свежие — первыми (при равной дате — по id).
        all.Rows.Select(r => r.DetectedAt).ShouldBeInOrder(SortDirection.Descending);
    }

    [Fact(DisplayName = "Классификатор: сид только на пустом, дубли уровня, третий уровень и удаление занятых — отказ")]
    public async Task Classifier_keeps_two_levels_and_protects_used_items()
    {
        var inspector = new InspectorContextFactory(_postgres.GetConnectionString());
        await MigrateAsync();
        var categories = new ViolationCategoryStore(inspector);

        // Сид на пустом; повторный — нет (администраторский набор неприкосновенен).
        (await categories.SeedDefaultsAsync()).ShouldBe(7);
        (await categories.SeedDefaultsAsync()).ShouldBe(0);

        var spheres = (await categories.ListAsync()).Where(c => c.ParentId is null).ToList();
        spheres.Count.ShouldBe(7);
        var sphere = spheres.Single(s => s.Name == "Документооборот");

        // Дубль на уровне; вид внутри вида (третий уровень) — отказ.
        (await categories.CreateAsync("Документооборот", null)).Result.ShouldBe(ViolationWriteResult.DuplicateName);
        var (created, kindId) = await categories.CreateAsync("Просрочка регистрации", sphere.Id);
        created.ShouldBe(ViolationWriteResult.Ok);
        (await categories.CreateAsync("Глубже нельзя", kindId)).Result.ShouldBe(ViolationWriteResult.CategoryNotLeaf);

        // Сфера с видом не удаляется; вид с нарушением — тоже; пустой вид — удаляется.
        (await categories.DeleteAsync(sphere.Id)).ShouldBe(ViolationWriteResult.InUse);

        var store = new ViolationStore(inspector);
        var divisionId = await SeedDivisionAsync("Альфа");
        (await store.CreateAsync(Draft(divisionId, kindId))).Result.ShouldBe(ViolationWriteResult.Ok);
        (await categories.DeleteAsync(kindId)).ShouldBe(ViolationWriteResult.InUse);

        var (_, emptyKindId) = await categories.CreateAsync("Пустой вид", sphere.Id);
        (await categories.DeleteAsync(emptyKindId)).ShouldBe(ViolationWriteResult.Ok);
    }

    private static ViolationDraft Draft(int divisionId, int categoryId, DateOnly? detectedAt = null) => new(
        divisionId, categoryId, ViolationSeverity.Medium, detectedAt ?? new DateOnly(2026, 5, 1),
        RemediationStatus.UnderControl, null, null, null, null, null);

    private async Task MigrateAsync()
    {
        await using (var db = new CoreContextFactory(_postgres.GetConnectionString()).CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        await using (var db = new InspectorContextFactory(_postgres.GetConnectionString()).CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }
    }

    private async Task<int> SeedDivisionAsync(string name)
    {
        var factory = new InspectorContextFactory(_postgres.GetConnectionString());
        await using var db = factory.CreateDbContext();
        var division = new Division { Name = name };
        db.Divisions.Add(division);
        await db.SaveChangesAsync();
        return division.Id;
    }

    private async Task<(ViolationStore Store, ViolationCategoryStore Categories, TestIds Ids)> BuildAsync()
    {
        await MigrateAsync();

        var inspector = new InspectorContextFactory(_postgres.GetConnectionString());
        var categories = new ViolationCategoryStore(inspector);

        var divisionA = await SeedDivisionAsync("Альфа");
        var divisionB = await SeedDivisionAsync("Бета");
        var (_, sphereId) = await categories.CreateAsync("Документооборот", null);
        var (_, kind1Id) = await categories.CreateAsync("Просрочка регистрации", sphereId);
        var (_, kind2Id) = await categories.CreateAsync("Утрата контроля", sphereId);

        return (new ViolationStore(inspector), categories,
            new TestIds(divisionA, divisionB, sphereId, kind1Id, kind2Id));
    }

    private sealed record TestIds(int DivisionA, int DivisionB, int Sphere, int KindK1, int KindK2);
}
