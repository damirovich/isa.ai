using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Data;
using ISC.AI.Modules.Media.Data.Entities;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using ISC.AI.Persistence.Storage;
using Shouldly;
using Xunit;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Оценки кадра лица (ТФ-МЕД-21, ADR-0058) на настоящем Postgres: индексация пишет их в <c>media.face</c>, каталог
/// отдаёт их в строке лица; у лица, проиндексированного без оценок, их нет — ранжирование тогда идёт по общему баллу.
/// </summary>
public sealed class MediaFrameMetricsTests(MediaSnapshotFixture fixture) : IClassFixture<MediaSnapshotFixture>
{
    [Fact(DisplayName = "ADR-0058: оценки кадра пишутся при индексации и читаются каталогом; лицо без оценок — без них; лучший кадр — по оценке")]
    public async Task Metrics_round_trip()
    {
        var storage = new LocalFileStorage(Path.Combine(fixture.StorageRoot, "frame-metrics"));
        var store = new MediaStore(fixture.Media, storage);
        var catalog = new MediaCatalog(fixture.Media, new AllowAllAccessPolicy());
        var access = InvestigationTestKit.Access(10, 9, 5);

        var photo = await store.ReceiveAsync(
            new MediaAssetDraft("лица.jpg", "image/jpeg", MediaKind.Image, 2, 5, "тест", null, 10),
            new MemoryStream([0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4]));
        var landmarks = new FaceLandmarks(new FacePoint(10, 10), new FacePoint(30, 10), new FacePoint(20, 20), new FacePoint(12, 28), new FacePoint(28, 28));
        var face = new DetectedFace(new BoundingBox(0, 0, 40, 40), landmarks, 0.95f);
        var metrics = new FrameMetrics(0.82f, 0.75f, -5.5f, 3.25f, 1.5f, 20f, 0.81f);
        await store.CompleteIndexingAsync(
            photo.AssetId,
            [
                new IndexedFace(null, null, face, new FaceQuality(0.9f, true, null), new float[FaceTemplate.Dimensions], Metrics: metrics),
                new IndexedFace(null, null, face with { Score = 0.9f }, new FaceQuality(0.99f, true, null), new float[FaceTemplate.Dimensions]),
            ],
            "yunet-1", "sface-1");

        var faces = await catalog.ListFacesAsync(photo.AssetId, access);

        faces.Count.ShouldBe(2);
        var scored = faces.Single(f => f.Frame is not null);
        scored.Frame.ShouldBe(metrics);
        faces.Single(f => f.Frame is null).QualityScore.ShouldBe(0.99f);
        FaceRanking.Best(faces)!.Id.ShouldBe(scored.Id); // оценённый кадр — раньше неоценённого, хоть у того общий балл выше
        (await catalog.GetFaceAsync(scored.Id, access)).ShouldNotBeNull().Frame.ShouldBe(metrics);
    }
}
