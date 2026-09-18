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
/// Каталог пакета «Медиа» (ТФ-МЕД-03, ТФ-ПЛ-03) под решёткой (ТБ-020/070): носитель, лица и шаблон выше
/// допуска или чужого подразделения неотличимы от несуществующих; счётчик лиц — по носителю; вектор
/// шаблона отдаётся только в допуске.
/// </summary>
/// <remarks>Требуется Docker.</remarks>
[Trait("Category", "Gate")]
public sealed class MediaCatalogTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = TestPostgres.Create();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "GATE-4: карточка, список, лица и шаблон видны только в допуске; недоступное = несуществующее")]
    public async Task Catalog_reads_apply_access_filter_on_db_side()
    {
        var factory = new MediaContextFactory(_postgres.GetConnectionString());
        int openAsset, secretAsset, foreignAsset, openFace, secretFace;
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
            (openAsset, openFace) = await SeedAsync(db, classification: 1, divisionId: 7, faces: 2);
            (secretAsset, secretFace) = await SeedAsync(db, classification: 2, divisionId: 7, faces: 1);
            (foreignAsset, _) = await SeedAsync(db, classification: 0, divisionId: 8, faces: 1);
        }

        var catalog = new MediaCatalog(factory, new AllowAllAccessPolicy());
        var access = new AccessContext("u1", MaxClassification: 1, AllowedDivisions: [7]);

        var row = await catalog.GetAsync(openAsset, access);
        row.ShouldNotBeNull();
        row.Id.ShouldBe(openAsset);
        row.FaceCount.ShouldBe(2);
        row.Kind.ShouldBe(MediaKind.Video);
        row.IndexStatus.ShouldBe(MediaIndexStatus.Indexed);

        (await catalog.GetAsync(secretAsset, access)).ShouldBeNull();
        (await catalog.GetAsync(foreignAsset, access)).ShouldBeNull();

        var list = await catalog.ListAsync([openAsset, secretAsset, foreignAsset], access);
        list.ShouldHaveSingleItem().Id.ShouldBe(openAsset);
        (await catalog.ListAsync([], access)).ShouldBeEmpty();

        var faces = await catalog.ListFacesAsync(openAsset, access);
        faces.Count.ShouldBe(2);
        faces.Select(f => f.FrameIndex).ShouldBe([0, 1]); // по кадру
        faces.ShouldAllBe(f => f.AssetId == openAsset && f.CropStoredFileName != null);
        (await catalog.ListFacesAsync(secretAsset, access)).ShouldBeEmpty();

        (await catalog.GetFaceAsync(openFace, access)).ShouldNotBeNull().FrameTimestampMs.ShouldBe(0);
        (await catalog.GetFaceAsync(secretFace, access)).ShouldBeNull();

        // Шаблон — только в допуске; вектор возвращается как есть (размерность схемы).
        var template = await catalog.GetTemplateAsync(openFace, access);
        template.ShouldNotBeNull();
        template.Length.ShouldBe(FaceTemplate.Dimensions);
        template[0].ShouldBe(1f);
        (await catalog.GetTemplateAsync(secretFace, access)).ShouldBeNull();

        // Повышенный допуск открывает секретный носитель того же подразделения, но не чужое подразделение.
        var cleared = access with { MaxClassification = 2 };
        (await catalog.GetAsync(secretAsset, cleared)).ShouldNotBeNull().FaceCount.ShouldBe(1);
        (await catalog.GetTemplateAsync(secretFace, cleared)).ShouldNotBeNull();
        (await catalog.GetAsync(foreignAsset, cleared)).ShouldBeNull();

        // Без контекста — отказ (fail-closed), не чтение без фильтра (ТБ-021).
        await Should.ThrowAsync<AccessContextRequiredException>(() => catalog.GetAsync(openAsset, null!));
        await Should.ThrowAsync<AccessContextRequiredException>(() => catalog.GetTemplateAsync(openFace, null!));
    }

    // Видео с N кадрами; на каждом — лицо с вырезкой и шаблоном [1,0,...]. Возвращает (носитель, первое лицо).
    private static async Task<(int AssetId, int FirstFaceId)> SeedAsync(MediaDbContext db, short classification, int divisionId, int faces)
    {
        var asset = new MediaAsset
        {
            Kind = MediaKind.Video,
            OriginalFileName = "v.mp4",
            StoredFileName = Guid.NewGuid().ToString("N") + ".mp4",
            ContentType = "video/mp4",
            ContentHash = Guid.NewGuid().ToString("N"),
            ByteSize = 1,
            DurationMs = 1000L * faces,
            Classification = classification,
            DivisionId = divisionId,
            IndexStatus = MediaIndexStatus.Indexed,
        };
        db.Assets.Add(asset);
        await db.SaveChangesAsync();

        var firstFaceId = 0;
        for (var i = 0; i < faces; i++)
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
                Classification = classification,
                DivisionId = divisionId,
            };
            db.Faces.Add(face);
            await db.SaveChangesAsync();
            if (i == 0)
            {
                firstFaceId = face.Id;
            }

            var values = new float[FaceTemplate.Dimensions];
            values[0] = 1f;
            db.Templates.Add(new FaceTemplate
            {
                FaceId = face.Id,
                AssetId = asset.Id,
                Embedding = new Vector(values),
                ModelVersion = "sface-test",
                QualityAcceptable = true,
                Classification = classification,
                DivisionId = divisionId,
            });
            await db.SaveChangesAsync();
        }

        return (asset.Id, firstFaceId);
    }
}
