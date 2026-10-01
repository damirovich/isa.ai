using System;
using System.Linq;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Data;
using ISC.AI.Modules.Media.Data.Entities;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Testcontainers.PostgreSql;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Отрезок трека лица (ТФ-ПЕР-02, ADR-0037) на настоящем Postgres: первый и последний кадр трека и число кадров;
/// кадры выше допуска в отрезок не входят; после переиндексации (прежнего лица нет) трек берётся у единственного лица
/// того же кадра, а при нескольких лицах на кадре — не угадывается; лицо без трека отрезка не даёт.
/// </summary>
/// <remarks>Требуется Docker.</remarks>
public sealed class MediaTrackSpanTests : IAsyncLifetime
{
    private const int MissingFace = 999_999;
    private const int AnotherMissingFace = 999_998;

    private static readonly AccessContext Insider = new("10", MaxClassification: 1, AllowedDivisions: [7]);

    private readonly PostgreSqlContainer _postgres = TestPostgres.Create();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "Отрезок трека: по видимым кадрам; после переиндексации — по единственному лицу кадра; при двух лицах и без трека — нет")]
    public async Task Track_spans_follow_frames_access_and_reindex_rule()
    {
        var factory = new MediaContextFactory(_postgres.GetConnectionString());
        int assetId, inTrack, untracked;
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
            assetId = await SeedVideoAsync(db);

            // Трек 1: кадры 1, 2 с и 3 с (последний — выше допуска «Инсайдера»); трек 2 — второе лицо на 3 с;
            // трек 3 — одно лицо на 9 с; лицо без трека на 4 с.
            inTrack = await SeedFaceAsync(db, assetId, 1000, track: 1);
            await SeedFaceAsync(db, assetId, 2000, track: 1);
            await SeedFaceAsync(db, assetId, 3000, track: 1, classification: 3);
            await SeedFaceAsync(db, assetId, 3000, track: 2);
            await SeedFaceAsync(db, assetId, 9000, track: 3);
            untracked = await SeedFaceAsync(db, assetId, 4000, track: null);
        }

        var catalog = new MediaCatalog(factory, new AllowAllAccessPolicy());
        var spans = await catalog.GetTrackSpansAsync(
            [
                new FaceTrackRequest(inTrack, assetId, 1000),
                new FaceTrackRequest(untracked, assetId, 4000),
                new FaceTrackRequest(MissingFace, assetId, 9000),        // прежнее лицо удалено переиндексацией
                new FaceTrackRequest(AnotherMissingFace, assetId, 3000), // на кадре 3 с два лица — не угадываем
            ],
            Insider);

        spans.Keys.Order().ToList().ShouldBe(new[] { inTrack, MissingFace }.Order().ToList());
        spans[inTrack].ShouldBe(new FaceTrackSpan(inTrack, 1, 1000, 2000, 2)); // кадр выше допуска не входит
        spans[MissingFace].ShouldBe(new FaceTrackSpan(MissingFace, 3, 9000, 9000, 1));

        (await catalog.GetTrackSpansAsync([], Insider)).ShouldBeEmpty();
    }

    private static async Task<int> SeedVideoAsync(MediaDbContext db)
    {
        var asset = new MediaAsset
        {
            Kind = MediaKind.Video,
            OriginalFileName = "v.mp4",
            StoredFileName = Guid.NewGuid().ToString("N") + ".mp4",
            ContentType = "video/mp4",
            ContentHash = Guid.NewGuid().ToString("N"),
            ByteSize = 1,
            Classification = 1,
            DivisionId = 7,
            IndexStatus = MediaIndexStatus.Indexed,
        };
        db.Assets.Add(asset);
        await db.SaveChangesAsync();
        return asset.Id;
    }

    private static async Task<int> SeedFaceAsync(MediaDbContext db, int assetId, long timestampMs, int? track, short classification = 1)
    {
        var frame = await db.Frames.FirstOrDefaultAsync(f => f.AssetId == assetId && f.TimestampMs == timestampMs);
        if (frame is null)
        {
            frame = new MediaFrame { AssetId = assetId, Index = (int)(timestampMs / 1000), TimestampMs = timestampMs };
            db.Frames.Add(frame);
            await db.SaveChangesAsync();
        }

        var face = new Face
        {
            AssetId = assetId,
            FrameId = frame.Id,
            BoxX = 1, BoxY = 1, BoxWidth = 50, BoxHeight = 50,
            Landmarks = new float[10],
            DetectionScore = 0.95f,
            QualityScore = 0.9f,
            QualityAcceptable = true,
            TrackId = track,
            Classification = classification,
            DivisionId = 7,
        };
        db.Faces.Add(face);
        await db.SaveChangesAsync();
        return face.Id;
    }
}
