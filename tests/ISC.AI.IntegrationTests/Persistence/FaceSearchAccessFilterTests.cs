using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Data;
using ISC.AI.Modules.Media.Data.Entities;
using ISC.AI.Modules.Media.Domain.Model;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using Shouldly;
using Testcontainers.PostgreSql;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// GATE-4 (ТБ-020/021/070): решётка доступа на шаблонах лиц применяется НА СТОРОНЕ БД. Шаблон выше
/// допуска, из чужого подразделения, с неактуального носителя или непригодного лица не выдаётся;
/// субъект без допуска получает ПУСТО — неотличимо от «лица нет в базе»; без контекста — отказ.
/// Реальный PostgreSQL+pgvector через Testcontainers, миграции пакета накатываются по-настоящему
/// (включая HNSW с ef_construction=512 на vector(128)).
/// </summary>
/// <remarks>Требуется Docker. Векторы — синтетические: проверяется ФИЛЬТРАЦИЯ, не качество моделей.</remarks>
[Trait("Category", "Gate")]
public sealed partial class FaceSearchAccessFilterTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = TestPostgres.Create();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "GATE-4: выше допуска / чужое подразделение / неактуальное / непригодное не выдаётся; нет доступа = пусто")]
    public async Task Face_search_enforces_access_filter_on_db_side()
    {
        var factory = new MediaContextFactory(_postgres.GetConnectionString());
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
            await SeedAsync(db);
        }

        var search = new PgVectorFaceSearch(factory, new AllowAllAccessPolicy());

        // Все шаблоны — ОДИН И ТОТ ЖЕ вектор: по расстоянию они неразличимы, отсеять может только решётка.
        var query = new FaceSearchQuery(Probe(), TopK: 50);

        // Субъект: допуск гриф ≤ 1, подразделение 7.
        var access = new AccessContext("u1", MaxClassification: 1, AllowedDivisions: [7]);
        var results = await search.SearchAsync(query, access);

        results.ShouldHaveSingleItem();
        results[0].Classification.ShouldBe<short>(1);
        results[0].DivisionId.ShouldBe(7);
        results[0].CosineDistance.ShouldBeLessThan(1e-6);
        results[0].Similarity.ShouldBe(1.0, tolerance: 1e-6);

        // Неразличимость (по контенту): субъект без подходящего допуска получает ПУСТО.
        // Своё подразделение, но допуск ниже любого шаблона в нём — ПУСТО, не «ближайшее из недоступных».
        var noAccess = new AccessContext("u2", MaxClassification: 0, AllowedDivisions: [7]);
        (await search.SearchAsync(query, noAccess)).ShouldBeEmpty();

        // Пустой список подразделений — тоже пусто (fail-closed по построению).
        var noDivisions = new AccessContext("u3", MaxClassification: 9, AllowedDivisions: []);
        (await search.SearchAsync(query, noDivisions)).ShouldBeEmpty();

        // Неактуальные — только по явному запросу (ADR-0013), и всё равно в пределах допуска.
        var withStale = await search.SearchAsync(query with { IncludeStale = true }, access);
        withStale.Count.ShouldBe(2);
        withStale.ShouldAllBe(c => c.Classification <= 1 && c.DivisionId == 7);
    }

    [Fact(DisplayName = "GATE-4: поиск без контекста доступа — отказ (fail-closed), не поиск без фильтра")]
    public async Task Face_search_without_access_context_fails_closed()
    {
        var factory = new MediaContextFactory(_postgres.GetConnectionString());
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        var search = new PgVectorFaceSearch(factory, new AllowAllAccessPolicy());

        await Should.ThrowAsync<AccessContextRequiredException>(
            () => search.SearchAsync(new FaceSearchQuery(Probe()), access: null!));
    }

    [Fact(DisplayName = "GATE-4: порог расстояния сужает выдачу ПОСЛЕ решётки, а не вместо неё")]
    public async Task Distance_threshold_applies_after_access_filter()
    {
        var factory = new MediaContextFactory(_postgres.GetConnectionString());
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
            await SeedAsync(db);
            // Ещё один доступный шаблон, но ДАЛЁКИЙ от пробы (ортогональный вектор).
            await AddAssetWithTemplateAsync(db, classification: 1, divisionId: 7, isCurrent: true,
                acceptable: true, vector: Orthogonal());
        }

        var search = new PgVectorFaceSearch(factory, new AllowAllAccessPolicy());
        var access = new AccessContext("u1", MaxClassification: 1, AllowedDivisions: [7]);

        var all = await search.SearchAsync(new FaceSearchQuery(Probe(), TopK: 50), access);
        all.Count.ShouldBe(2);
        all[0].CosineDistance.ShouldBeLessThan(all[1].CosineDistance); // ближайший — первым

        var near = await search.SearchAsync(new FaceSearchQuery(Probe(), TopK: 50, MaxCosineDistance: 0.5), access);
        near.ShouldHaveSingleItem().CosineDistance.ShouldBeLessThan(1e-6);
    }
}
