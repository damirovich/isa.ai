using ISC.AI.Abstractions.Audit;
using ISC.AI.Modules.Media.Data;
using ISC.AI.Modules.Media.Data.Entities;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using ISC.AI.Persistence.Audit;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using Shouldly;
using Testcontainers.PostgreSql;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// GATE-6 (ТБ-064/075): гарантированное удаление носителя и ВСЕХ биометрических производных —
/// кадров, лиц, шаблонов в pgvector, файлов исходника и вырезок — физически, с записью в аудит ДО
/// уничтожения. Каскад БД (<c>ON DELETE CASCADE</c>) — настоящий; журнал — настоящий (схема core).
/// </summary>
/// <remarks>Требуется Docker.</remarks>
[Trait("Category", "Gate")]
public sealed class MediaPurgeTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = TestPostgres.Create();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "GATE-6: носитель, кадры, лица, шаблоны и файлы удалены физически; факт — в аудите; соседний носитель цел")]
    public async Task Purge_removes_asset_and_all_derivatives_and_audits()
    {
        var media = new MediaContextFactory(_postgres.GetConnectionString());
        var core = new CoreContextFactory(_postgres.GetConnectionString());
        int targetId, otherId;
        await using (var db = media.CreateDbContext())
        {
            await db.Database.MigrateAsync();
            targetId = await SeedVideoAsync(db, "target");
            otherId = await SeedVideoAsync(db, "other");
        }

        await using (var db = core.CreateDbContext())
        {
            await db.Database.MigrateAsync(); // журнал аудита живёт в схеме ядра
        }

        var storage = new RecordingFileStorage();
        var purger = new MediaPurger(media, new AuditWriter(core), storage);

        var result = await purger.PurgeAsync(targetId, subjectId: 42);

        result.Found.ShouldBeTrue();
        result.FacesRemoved.ShouldBe(2);
        result.FilesRemoved.ShouldBe(3); // исходник + 2 вырезки

        await using (var db = media.CreateDbContext())
        {
            (await db.Assets.AnyAsync(a => a.Id == targetId)).ShouldBeFalse();
            (await db.Frames.AnyAsync(f => f.AssetId == targetId)).ShouldBeFalse();
            (await db.Faces.AnyAsync(f => f.AssetId == targetId)).ShouldBeFalse();
            (await db.Templates.AnyAsync(t => t.AssetId == targetId)).ShouldBeFalse();

            // Соседний носитель со всеми производными уцелел.
            (await db.Assets.AnyAsync(a => a.Id == otherId)).ShouldBeTrue();
            (await db.Faces.CountAsync(f => f.AssetId == otherId)).ShouldBe(2);
            (await db.Templates.CountAsync(t => t.AssetId == otherId)).ShouldBe(2);
        }

        // Файлы: исходник и обе вырезки — в подкаталоге носителя.
        storage.Deleted.Count.ShouldBe(3);
        storage.Deleted.ShouldContain($"{MediaFileCategories.Originals}/{targetId}/target.mp4");
        storage.Deleted.Count(f => f.StartsWith($"{MediaFileCategories.FaceCrops}/{targetId}/")).ShouldBe(2);

        await using (var db = core.CreateDbContext())
        {
            var audit = await db.AuditRecords.SingleAsync(r => r.Action == AuditAction.Purge);
            audit.ObjectRef.ShouldBe($"media:asset:{targetId}");
            audit.SubjectId.ShouldBe(42);
            audit.Classification.ShouldBe<short>(2);
            audit.DivisionId.ShouldBe(7);
        }
    }

    [Fact(DisplayName = "GATE-6: удаление несуществующего носителя идемпотентно, аудит и хранилище не трогаются")]
    public async Task Purge_of_missing_asset_is_idempotent_without_audit()
    {
        var media = new MediaContextFactory(_postgres.GetConnectionString());
        var core = new CoreContextFactory(_postgres.GetConnectionString());
        await using (var db = media.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        await using (var db = core.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        var storage = new RecordingFileStorage();
        var purger = new MediaPurger(media, new AuditWriter(core), storage);

        var result = await purger.PurgeAsync(assetId: 999_999, subjectId: 7);

        result.Found.ShouldBeFalse();
        storage.Deleted.ShouldBeEmpty();
        await using (var db = core.CreateDbContext())
        {
            (await db.AuditRecords.AnyAsync()).ShouldBeFalse();
        }
    }

    [Fact(DisplayName = "ТФ-ДЕЛ-04/ТБ-074: снятие биометрии удаляет шаблоны и вырезки, но оставляет носитель, кадры и лица; повтор идемпотентен")]
    public async Task Purge_templates_removes_biometrics_and_keeps_case_materials()
    {
        var media = new MediaContextFactory(_postgres.GetConnectionString());
        var core = new CoreContextFactory(_postgres.GetConnectionString());
        int closedCaseAsset, otherCaseAsset;
        await using (var db = media.CreateDbContext())
        {
            await db.Database.MigrateAsync();
            closedCaseAsset = await SeedVideoAsync(db, "closed-case");
            otherCaseAsset = await SeedVideoAsync(db, "other-case");
        }

        await using (var db = core.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        var storage = new RecordingFileStorage();
        var purger = new MediaPurger(media, new AuditWriter(core), storage);

        var result = await purger.PurgeTemplatesAsync([closedCaseAsset], "закрытие дела №1", subjectId: 42);

        result.AssetsAffected.ShouldBe(1);
        result.TemplatesRemoved.ShouldBe(2);
        result.CropsRemoved.ShouldBe(2);

        await using (var db = media.CreateDbContext())
        {
            // Ушло ровно то, чем ведётся поиск: векторов этого носителя в базе не осталось.
            (await db.Templates.AnyAsync(t => t.AssetId == closedCaseAsset)).ShouldBeFalse();

            // Материалы дела на месте: носитель, кадры и сами лица с координатами и привязками —
            // это результат расследования, он хранится по правилам дела (ТФ-ДЕЛ-04).
            (await db.Assets.AnyAsync(a => a.Id == closedCaseAsset)).ShouldBeTrue();
            (await db.Frames.CountAsync(f => f.AssetId == closedCaseAsset)).ShouldBe(2);
            (await db.Faces.CountAsync(f => f.AssetId == closedCaseAsset)).ShouldBe(2);

            // Ссылка на вырезку обнулена — файла больше нет, и страница не должна его запрашивать.
            (await db.Faces.AnyAsync(f => f.AssetId == closedCaseAsset && f.CropStoredFileName != null)).ShouldBeFalse();

            // Носитель другого (открытого) дела не затронут ничем.
            (await db.Templates.CountAsync(t => t.AssetId == otherCaseAsset)).ShouldBe(2);
            (await db.Faces.CountAsync(f => f.AssetId == otherCaseAsset && f.CropStoredFileName != null)).ShouldBe(2);
        }

        // Удалены только вырезки; исходник носителя остался в хранилище.
        storage.Deleted.Count.ShouldBe(2);
        storage.Deleted.ShouldAllBe(f => f.StartsWith($"{MediaFileCategories.FaceCrops}/{closedCaseAsset}/"));

        await using (var db = core.CreateDbContext())
        {
            var audit = await db.AuditRecords.SingleAsync(r => r.Action == AuditAction.Purge);
            audit.ObjectRef.ShouldBe($"media:asset:{closedCaseAsset}");
            audit.SubjectId.ShouldBe(42);
            audit.Classification.ShouldBe<short>(2); // гриф носителя, не усреднённый по делу
            audit.DivisionId.ShouldBe(7);
        }

        // Повтор (например, повторное закрытие дела): удалять нечего — ни изменений, ни новых записей.
        var again = await purger.PurgeTemplatesAsync([closedCaseAsset], "закрытие дела №1", subjectId: 42);

        again.ShouldBe(TemplatePurgeResult.Empty);
        storage.Deleted.Count.ShouldBe(2);
        await using (var db = core.CreateDbContext())
        {
            (await db.AuditRecords.CountAsync(r => r.Action == AuditAction.Purge)).ShouldBe(1);
        }
    }

    // Видео с двумя кадрами, на каждом — лицо с вырезкой и шаблоном. Гриф 2, подразделение 7.
    private static async Task<int> SeedVideoAsync(MediaDbContext db, string name)
    {
        var asset = new MediaAsset
        {
            Kind = MediaKind.Video,
            OriginalFileName = name + ".mp4",
            StoredFileName = name + ".mp4",
            ContentType = "video/mp4",
            ContentHash = Guid.NewGuid().ToString("N"),
            ByteSize = 1,
            DurationMs = 2000,
            Classification = 2,
            DivisionId = 7,
            IndexStatus = MediaIndexStatus.Indexed,
        };
        db.Assets.Add(asset);
        await db.SaveChangesAsync();

        for (var i = 0; i < 2; i++)
        {
            var frame = new MediaFrame { AssetId = asset.Id, Index = i, TimestampMs = i * 1000 };
            db.Frames.Add(frame);
            await db.SaveChangesAsync();

            var face = new Face
            {
                AssetId = asset.Id,
                FrameId = frame.Id,
                BoxX = 1, BoxY = 1, BoxWidth = 40, BoxHeight = 40,
                Landmarks = new float[10],
                DetectionScore = 0.9f,
                QualityScore = 0.8f,
                QualityAcceptable = true,
                CropStoredFileName = Guid.NewGuid().ToString("N") + ".jpg",
                Classification = 2,
                DivisionId = 7,
            };
            db.Faces.Add(face);
            await db.SaveChangesAsync();

            var values = new float[FaceTemplate.Dimensions];
            values[i] = 1f;
            db.Templates.Add(new FaceTemplate
            {
                FaceId = face.Id,
                AssetId = asset.Id,
                Embedding = new Vector(values),
                ModelVersion = "sface-test",
                QualityAcceptable = true,
                Classification = 2,
                DivisionId = 7,
            });
            await db.SaveChangesAsync();
        }

        return asset.Id;
    }
}
