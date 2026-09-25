using System;
using System.IO;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Data;
using ISC.AI.Modules.Media.Data.Entities;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Данные покадрового просмотра и снимка кадра (ADR-0028) на настоящей БД: приём переносит происхождение снимка
/// (видео-источник, момент); результат индексации с пробой ffprobe пишет частоту, размер кадра и ТОЧНУЮ длительность
/// поверх прежних значений, без пробы — поля пробы не трогает; каталог отдаёт новые поля; резолвер источника кадра
/// несёт режимные поля носителя, к которым floor ядра применим напрямую (ТБ-020/021).
/// </summary>
/// <remarks>Требуется Docker. Контейнер общий на класс (<see cref="MediaTranscriptFixture"/>): тесты работают со своими носителями.</remarks>
[Trait("Category", "Gate")]
public sealed class MediaVideoFrameInfoTests(MediaTranscriptFixture fixture) : IClassFixture<MediaTranscriptFixture>
{
    [Fact(DisplayName = "ADR-0028: приём переносит происхождение снимка — видео-источник и момент записи; у обычной загрузки — пусто")]
    public async Task Receive_carries_snapshot_provenance()
    {
        var store = new MediaStore(fixture.Media, new RecordingFileStorage());

        var video = await store.ReceiveAsync(Draft("clip.mkv", "video/x-matroska", MediaKind.Video, division: 301), new MemoryStream([1, 2, 3]));
        var snapshot = await store.ReceiveAsync(
            Draft("кадр.png", "image/png", MediaKind.Image, division: 301) with
            {
                SourceAssetId = video.AssetId,
                SourceTimestampMs = 41_960,
            },
            new MemoryStream([4, 5, 6]));
        snapshot.Duplicate.ShouldBeFalse();

        await using var db = await fixture.Media.CreateDbContextAsync();
        var videoRow = await db.Assets.AsNoTracking().SingleAsync(a => a.Id == video.AssetId);
        videoRow.SourceAssetId.ShouldBeNull();
        videoRow.SourceTimestampMs.ShouldBeNull();
        videoRow.FrameRate.ShouldBeNull(); // до пробы частоты нет

        var snapshotRow = await db.Assets.AsNoTracking().SingleAsync(a => a.Id == snapshot.AssetId);
        snapshotRow.Kind.ShouldBe(MediaKind.Image);
        snapshotRow.SourceAssetId.ShouldBe(video.AssetId);
        snapshotRow.SourceTimestampMs.ShouldBe(41_960);
        snapshotRow.IndexStatus.ShouldBe(MediaIndexStatus.Uploaded); // снимок индексируется как загруженное фото
    }

    [Fact(DisplayName = "ADR-0028: результат индексации с пробой пишет частоту, размер кадра и точную длительность ПОВЕРХ; без пробы — поля пробы не трогаются, длительность — запасная, если передана")]
    public async Task Complete_indexing_writes_probe_over_previous_values()
    {
        var store = new MediaStore(fixture.Media, new RecordingFileStorage());
        var video = await store.ReceiveAsync(Draft("clip.mov", "video/quicktime", MediaKind.Video, division: 302), new MemoryStream([9, 9, 9]));
        var id = video.AssetId;

        // 1. Без пробы (до ADR-0028): длительность — таймкод последнего кадра выборки, полей пробы нет.
        await store.CompleteIndexingAsync(id, [], "yunet-1", "sface-1", durationMs: 3000);
        var row = await RowAsync(id);
        row.DurationMs.ShouldBe(3000);
        row.FrameRate.ShouldBeNull();
        row.FrameWidth.ShouldBeNull();
        row.FrameHeight.ShouldBeNull();

        // 2. С пробой: частота, размер после автоповорота и ТОЧНАЯ длительность — поверх, даже если раскадровка дала своё.
        var probe = new VideoProbe(29.97, TimeSpan.FromMilliseconds(12_345), Width: 1080, Height: 1920);
        await store.CompleteIndexingAsync(id, [], "yunet-1", "sface-1", durationMs: 3000, probe: probe);
        row = await RowAsync(id);
        row.DurationMs.ShouldBe(12_345);
        row.FrameRate.ShouldBe(29.97);
        row.FrameWidth.ShouldBe(1080);
        row.FrameHeight.ShouldBe(1920);
        row.IndexStatus.ShouldBe(MediaIndexStatus.Indexed);

        // 3. Снова без пробы и без длительности — ничего из записанного не теряется.
        await store.CompleteIndexingAsync(id, [], "yunet-2", "sface-2");
        row = await RowAsync(id);
        row.DurationMs.ShouldBe(12_345);
        row.FrameRate.ShouldBe(29.97);
        row.FrameWidth.ShouldBe(1080);
        row.FrameHeight.ShouldBe(1920);
        row.DetectorVersion.ShouldBe("yunet-2");

        // 4. Переиндексация со СБОЙНОЙ пробой (probe = null), но с оценкой по раскадровке: точная длительность прежней
        //    пробы НЕ огрубляется оценкой (частота известна — значит, длительность точная), поля пробы нетронуты.
        await store.CompleteIndexingAsync(id, [], "yunet-2", "sface-2", durationMs: 4000);
        row = await RowAsync(id);
        row.DurationMs.ShouldBe(12_345);
        row.FrameRate.ShouldBe(29.97);
        row.FrameWidth.ShouldBe(1080);
        row.FrameHeight.ShouldBe(1920);
    }

    [Fact(DisplayName = "ADR-0028: проба без длительности (незавершённый Matroska/WebM) пишет DurationMs = null КАК ЕСТЬ — не 0, не оценку по раскадровке и не прежнее значение: при известной частоте длительность считается точной")]
    public async Task Probe_without_duration_writes_null_as_is()
    {
        new VideoProbe(30, TimeSpan.Zero, 1, 1).DurationMs.ShouldBeNull(); // ноль — не длительность
        new VideoProbe(30, null, 1, 1).DurationMs.ShouldBeNull();

        var store = new MediaStore(fixture.Media, new RecordingFileStorage());
        var noDuration = new VideoProbe(30, Duration: null, Width: 640, Height: 480);

        // Свежий носитель: проба без длительности + оценка по раскадровке → длительность null (оценка, округлённая
        // вниз до шага выборки, при известной частоте отрезала бы хвост записи в эндпоинте и снимке); частота/размер — из пробы.
        var first = await store.ReceiveAsync(Draft("cut.mkv", "video/x-matroska", MediaKind.Video, division: 306), new MemoryStream([8, 8, 8]));
        await store.CompleteIndexingAsync(first.AssetId, [], "yunet-1", "sface-1", durationMs: 5000, probe: noDuration);
        var row = await RowAsync(first.AssetId);
        row.DurationMs.ShouldBeNull();
        row.FrameRate.ShouldBe(30);
        row.FrameWidth.ShouldBe(640);
        row.FrameHeight.ShouldBe(480);

        // Проба с точной длительностью — записана; следующая проба без длительности — снова null, как есть.
        await store.CompleteIndexingAsync(first.AssetId, [], "yunet-1", "sface-1",
            probe: new VideoProbe(30, TimeSpan.FromMilliseconds(5_432), 640, 480));
        (await RowAsync(first.AssetId)).DurationMs.ShouldBe(5_432);
        await store.CompleteIndexingAsync(first.AssetId, [], "yunet-1", "sface-1", durationMs: 5000, probe: noDuration);
        (await RowAsync(first.AssetId)).DurationMs.ShouldBeNull();

        // Сбойная проба (null) при известной частоте — длительность не меняется (ни оценкой, ни обнулением).
        await store.CompleteIndexingAsync(first.AssetId, [], "yunet-1", "sface-1",
            probe: new VideoProbe(30, TimeSpan.FromMilliseconds(5_432), 640, 480));
        await store.CompleteIndexingAsync(first.AssetId, [], "yunet-1", "sface-1", durationMs: 5000);
        (await RowAsync(first.AssetId)).DurationMs.ShouldBe(5_432);

        // Ни пробы длительности, ни оценки — null, а не 0 (иначе просмотр запёрся бы на кадре 0).
        var second = await store.ReceiveAsync(Draft("cut2.webm", "video/webm", MediaKind.Video, division: 306), new MemoryStream([9, 9, 8]));
        await store.CompleteIndexingAsync(second.AssetId, [], "yunet-1", "sface-1", probe: noDuration);
        row = await RowAsync(second.AssetId);
        row.DurationMs.ShouldBeNull();
        row.FrameRate.ShouldBe(30);
    }

    [Fact(DisplayName = "ADR-0028: каталог отдаёт частоту, размер кадра и происхождение снимка (карточка и список) под решёткой")]
    public async Task Catalog_returns_frame_info_and_provenance()
    {
        var store = new MediaStore(fixture.Media, new RecordingFileStorage());
        var video = await store.ReceiveAsync(Draft("clip.avi", "video/x-msvideo", MediaKind.Video, division: 303), new MemoryStream([5, 5, 5]));
        await store.CompleteIndexingAsync(
            video.AssetId, [], "yunet-1", "sface-1", probe: new VideoProbe(25, TimeSpan.FromSeconds(8), 1280, 720));
        var snapshot = await store.ReceiveAsync(
            Draft("кадр.png", "image/png", MediaKind.Image, division: 303) with { SourceAssetId = video.AssetId, SourceTimestampMs = 2_040 },
            new MemoryStream([6, 6, 6]));

        var catalog = new MediaCatalog(fixture.Media, new AllowAllAccessPolicy());
        var access = new AccessContext("u", MaxClassification: 1, AllowedDivisions: [303]);

        var videoRow = (await catalog.GetAsync(video.AssetId, access)).ShouldNotBeNull();
        videoRow.FrameRate.ShouldBe(25);
        videoRow.FrameWidth.ShouldBe(1280);
        videoRow.FrameHeight.ShouldBe(720);
        videoRow.DurationMs.ShouldBe(8000);
        videoRow.SourceAssetId.ShouldBeNull();
        videoRow.SourceTimestampMs.ShouldBeNull();

        var snapshotRow = (await catalog.GetAsync(snapshot.AssetId, access)).ShouldNotBeNull();
        snapshotRow.FrameRate.ShouldBeNull();
        snapshotRow.SourceAssetId.ShouldBe(video.AssetId);
        snapshotRow.SourceTimestampMs.ShouldBe(2_040);

        var list = await catalog.ListAsync([video.AssetId, snapshot.AssetId], access);
        list.Count.ShouldBe(2);
        list.ShouldContain(r => r.Id == snapshot.AssetId && r.SourceAssetId == video.AssetId && r.SourceTimestampMs == 2_040);
        list.ShouldContain(r => r.Id == video.AssetId && r.FrameRate == 25 && r.FrameWidth == 1280);

        // Чужое подразделение — носители неотличимы от несуществующих (ТБ-020).
        (await catalog.GetAsync(snapshot.AssetId, access with { AllowedDivisions = [304] })).ShouldBeNull();
    }

    [Fact(DisplayName = "ADR-0028/ТБ-020: источник кадра — оригинал, вид, длительность, частота и режимные поля носителя; floor ядра применим напрямую; носителя нет — null")]
    public async Task Frame_source_resolves_with_access_fields()
    {
        var store = new MediaStore(fixture.Media, new RecordingFileStorage());
        var video = await store.ReceiveAsync(
            Draft("clip.3gp", "video/3gpp", MediaKind.Video, division: 305) with { Classification = 2 }, new MemoryStream([3, 3, 3]));
        await store.CompleteIndexingAsync(
            video.AssetId, [], "yunet-1", "sface-1", probe: new VideoProbe(15, TimeSpan.FromMilliseconds(6_500), 176, 144));
        var image = await store.ReceiveAsync(Draft("a.png", "image/png", MediaKind.Image, division: 305), new MemoryStream([2, 2, 2]));

        var resolver = new MediaFileAccessResolver(fixture.Media);

        var source = (await resolver.ResolveFrameSourceAsync(video.AssetId)).ShouldNotBeNull();
        source.AssetId.ShouldBe(video.AssetId);
        source.StoredFileName.ShouldEndWith(".3gp");
        source.Kind.ShouldBe(MediaKind.Video);
        source.ContentType.ShouldBe("video/3gpp");
        source.Classification.ShouldBe<short>(2);
        source.DivisionId.ShouldBe(305);
        source.DurationMs.ShouldBe(6_500);
        source.FrameRate.ShouldBe(15);

        // Резолвер решётку не применяет — её применяет эндпоинт по этим полям: допуск ниже грифа и чужое подразделение — отказ.
        BaselineAccess.Filter<MediaFrameSource>(new AccessContext("u", 1, [305])).Compile()(source).ShouldBeFalse();
        BaselineAccess.Filter<MediaFrameSource>(new AccessContext("u", 2, [306])).Compile()(source).ShouldBeFalse();
        BaselineAccess.Filter<MediaFrameSource>(new AccessContext("u", 2, [305])).Compile()(source).ShouldBeTrue();

        // Не видео разрешается (вид проверяет эндпоинт), несуществующий — null.
        (await resolver.ResolveFrameSourceAsync(image.AssetId)).ShouldNotBeNull().Kind.ShouldBe(MediaKind.Image);
        (await resolver.ResolveFrameSourceAsync(999_999)).ShouldBeNull();
    }

    private async Task<MediaAsset> RowAsync(int id)
    {
        await using var db = await fixture.Media.CreateDbContextAsync();
        return await db.Assets.AsNoTracking().SingleAsync(a => a.Id == id);
    }

    private static MediaAssetDraft Draft(string fileName, string contentType, MediaKind kind, int division) => new(
        OriginalFileName: fileName,
        ContentType: contentType,
        Kind: kind,
        Classification: 1,
        DivisionId: division,
        Source: "тест",
        CapturedAt: null,
        UploadedByUserId: 10);
}
