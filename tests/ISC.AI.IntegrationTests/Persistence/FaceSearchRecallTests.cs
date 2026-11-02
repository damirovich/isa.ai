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
/// Полнота поиска по лицу (ЦС-02, ТО-мат-08, ADR-0019 v0.2) на настоящем PostgreSQL+pgvector: совпадение в области
/// поиска находится, даже если ближе к пробе больше <c>ef_search</c> шаблонов ВНЕ области — например, кадры того же
/// человека в закрытом деле или в деле чужого подразделения. HNSW отдаёт <c>ef_search</c> ближайших по всей таблице и
/// лишь потом применяет решётку и область, поэтому без точного перебора выдача в таком случае пуста (проба 02.11.2026).
/// </summary>
/// <remarks>
/// Требуется Docker. Векторы синтетические и детерминированные (фиксированное зерно): около 8 тыс. фоновых шаблонов в
/// области — столько, что планировщик сам выбирает обход HNSW, — 300 шаблонов вне области рядом с пробой
/// (схожесть ~0,9) и три искомых в области подальше от неё (~0,5); фон с пробой почти не схож (~0 ± 0,09).
/// </remarks>
public sealed class FaceSearchRecallTests : IAsyncLifetime
{
    private const int BackgroundAssets = 160;
    private const int FacesPerAsset = 50; // как кадры одного видео
    private const int NearOutOfScopeAssets = 6; // 300 шаблонов вне области — больше ширины обхода ef_search = 200

    private readonly PostgreSqlContainer _postgres = TestPostgres.Create();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "ADR-0019 v0.2: совпадение в области находится, хотя ближе к пробе сотни шаблонов вне области — точным перебором до предела и итеративным обходом HNSW выше него")]
    public async Task Scope_matches_are_found_behind_out_of_scope_neighbours()
    {
        var factory = new MediaContextFactory(_postgres.GetConnectionString());
        var seed = await SeedAsync(factory);
        var access = new AccessContext("u1", MaxClassification: 1, AllowedDivisions: [7]);

        // Предел по умолчанию (1 млн) больше таблицы — точный перебор при любой области.
        var exact = new PgVectorFaceSearch(factory, new AllowAllAccessPolicy());

        // Широкая область — все носители, кроме «чужих»: здесь обход HNSW без точного перебора отдавал пустую выдачу.
        ShouldFindTargets(await exact.SearchAsync(new FaceSearchQuery(Probe(), TopK: 20, AssetIds: seed.Scope), access), seed);

        // Узкая область — носители искомых: результат тот же.
        ShouldFindTargets(await exact.SearchAsync(new FaceSearchQuery(Probe(), TopK: 20, AssetIds: seed.TargetAssets), access), seed);

        // Таблица (8,3 тыс.) больше предела 1 000: узкая область (153 кандидата) — всё ещё точный перебор, решает подсчёт
        // кандидатов; широкая — HNSW с итеративным обходом pgvector 0.8, который доходит до совпадений за «чужими».
        var indexed = new PgVectorFaceSearch(factory, new AllowAllAccessPolicy(), exactScanLimit: 1000);
        ShouldFindTargets(await indexed.SearchAsync(new FaceSearchQuery(Probe(), TopK: 20, AssetIds: seed.TargetAssets), access), seed);
        ShouldFindTargets(await indexed.SearchAsync(new FaceSearchQuery(Probe(), TopK: 20, AssetIds: seed.Scope), access), seed);
    }

    // Искомые — три ближайших в области (фон с пробой почти не схож), и ни одного кандидата вне области.
    private static void ShouldFindTargets(IReadOnlyList<FaceCandidate> found, Seed seed)
    {
        found.Count.ShouldBe(20);
        found.Take(3).Select(c => c.FaceId).ShouldBe(seed.Targets, ignoreOrder: true);
        found.ShouldAllBe(c => seed.Scope.Contains(c.AssetId));
    }

    /// <summary>Посеянные данные: область поиска, искомые лица и носители, на которых они лежат.</summary>
    private sealed record Seed(int[] Scope, int[] Targets, int[] TargetAssets);

    // Проба — единичный вектор [1, 0, …]: схожесть с ним задаётся разбросом остальных координат.
    private static float[] Probe()
    {
        var v = new float[FaceTemplate.Dimensions];
        v[0] = 1f;
        return v;
    }

    // Около пробы: первая координата 1, остальные — равномерно в ±spread (cos ≈ 1/√(1 + 42,3·spread²)).
    private static float[] NearProbe(Random random, double spread)
    {
        var v = new float[FaceTemplate.Dimensions];
        v[0] = 1f;
        for (var i = 1; i < v.Length; i++)
        {
            v[i] = (float)((random.NextDouble() * 2 - 1) * spread);
        }

        return v;
    }

    // Фон: все координаты равномерно в ±1 — с пробой почти не схож.
    private static float[] Background(Random random)
    {
        var v = new float[FaceTemplate.Dimensions];
        for (var i = 0; i < v.Length; i++)
        {
            v[i] = (float)(random.NextDouble() * 2 - 1);
        }

        return v;
    }

    private static async Task<Seed> SeedAsync(MediaContextFactory factory)
    {
        await using var db = factory.CreateDbContext();
        await db.Database.MigrateAsync();

        // HNSW из миграции растёт вставкой по строке — на тысячах шаблонов это десятки секунд. Как при массовой загрузке,
        // индекс снимается и строится заново, тем же определением из каталога (параметры — из миграции, ADR-0019 п. 2).
        var index = await db.Database.SqlQuery<string>($"""
            SELECT i.indexrelid::regclass::text AS "Value"
            FROM pg_index i
            JOIN pg_class c ON c.oid = i.indexrelid
            JOIN pg_am a ON a.oid = c.relam
            WHERE i.indrelid = 'media.face_template'::regclass AND a.amname = 'hnsw'
            """).SingleAsync();
        var definition = await db.Database.SqlQuery<string>(
            $"""SELECT pg_get_indexdef({index}::regclass) AS "Value" """).SingleAsync();
        await db.Database.ExecuteSqlRawAsync("DROP INDEX " + index);

        var random = new Random(20261102);
        var assets = Enumerable.Range(0, BackgroundAssets + NearOutOfScopeAssets).Select(_ => NewAsset()).ToList();
        db.Assets.AddRange(assets);
        await db.SaveChangesAsync();

        var background = assets.Take(BackgroundAssets).ToList();
        var outOfScope = assets.Skip(BackgroundAssets).ToList();
        foreach (var asset in background)
        {
            for (var i = 0; i < FacesPerAsset; i++)
            {
                AddFace(db, asset, Background(random));
            }
        }

        foreach (var asset in outOfScope)
        {
            for (var i = 0; i < FacesPerAsset; i++)
            {
                AddFace(db, asset, NearProbe(random, spread: 0.0745)); // схожесть ~0,9
            }
        }

        var targets = background.Take(3).Select(asset => AddFace(db, asset, NearProbe(random, spread: 0.266))).ToList(); // ~0,5
        await db.SaveChangesAsync();

        // Сборка без параллельных исполнителей: им нужна разделяемая память, а у контейнера её 64 МБ.
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await db.Database.ExecuteSqlRawAsync("SET LOCAL max_parallel_maintenance_workers = 0");
            await db.Database.ExecuteSqlRawAsync(definition);
            await transaction.CommitAsync();
        }

        // Статистика для планировщика — как после автоанализа на рабочей базе.
        await db.Database.ExecuteSqlRawAsync("ANALYZE media.face_template");

        return new Seed(
            Scope: [.. background.Select(a => a.Id)],
            Targets: [.. targets.Select(t => t.Face!.Id)],
            TargetAssets: [.. background.Take(3).Select(a => a.Id)]);
    }

    private static MediaAsset NewAsset() => new()
    {
        Kind = MediaKind.Video,
        OriginalFileName = "v.mp4",
        StoredFileName = Guid.NewGuid().ToString("N") + ".mp4",
        ContentType = "video/mp4",
        ContentHash = Guid.NewGuid().ToString("N"),
        ByteSize = 1,
        Classification = 1,
        DivisionId = 7,
        IsCurrent = true,
        IndexStatus = MediaIndexStatus.Indexed,
    };

    // Лицо и шаблон добавляются вместе: ключ лица EF проставит в шаблон при сохранении.
    private static FaceTemplate AddFace(MediaDbContext db, MediaAsset asset, float[] vector)
    {
        var face = new Face
        {
            AssetId = asset.Id,
            BoxX = 1, BoxY = 1, BoxWidth = 50, BoxHeight = 50,
            Landmarks = new float[10],
            DetectionScore = 0.95f,
            QualityScore = 0.9f,
            QualityAcceptable = true,
            Classification = asset.Classification,
            DivisionId = asset.DivisionId,
        };
        var template = new FaceTemplate
        {
            Face = face,
            AssetId = asset.Id,
            Embedding = new Vector(vector),
            ModelVersion = "sface-test",
            QualityAcceptable = true,
            Classification = asset.Classification,
            DivisionId = asset.DivisionId,
            IsCurrent = true,
        };
        db.Templates.Add(template);
        return template;
    }
}
