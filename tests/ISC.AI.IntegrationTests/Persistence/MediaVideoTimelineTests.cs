using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Data;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using ISC.AI.Persistence.Audit;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Лента видео на настоящей БД (ADR-0038, ТФ-МЕД-11): результат индексации пишет ленту кадров и встроенное время записи
/// (поверх прежних; без новой ленты — прежняя остаётся), каталог отдаёт их; файл ленты разрешается только своему
/// носителю с его режимными полями; уничтожение носителя снимает и файл ленты (ТБ-064/075).
/// </summary>
/// <remarks>Требуется Docker. Контейнер общий на класс (<see cref="MediaTranscriptFixture"/>): тесты работают со своими носителями.</remarks>
[Trait("Category", "Gate")]
public sealed class MediaVideoTimelineTests(MediaTranscriptFixture fixture) : IClassFixture<MediaTranscriptFixture>
{
    private static readonly DateTimeOffset Recorded = new(2022, 12, 8, 8, 8, 12, TimeSpan.Zero);

    [Fact(DisplayName = "Индексация пишет ленту кадров и время записи; без новой ленты прежняя остаётся; проба без времени — время снимается; каталог отдаёт поля")]
    public async Task Complete_indexing_writes_filmstrip_and_recorded_time()
    {
        var store = new MediaStore(fixture.Media, new RecordingFileStorage());
        var video = await store.ReceiveAsync(Draft("clip.mp4", division: 401), new MemoryStream([1, 4, 1]));
        var id = video.AssetId;

        await store.CompleteIndexingAsync(
            id, [], "yunet-1", "sface-1",
            probe: new VideoProbe(25, TimeSpan.FromSeconds(40), 1280, 720, Recorded),
            filmstrip: new FilmstripDraft("aaaabbbbccccddddaaaabbbbccccdddd.jpg", 40, 1_000));

        var catalog = new MediaCatalog(fixture.Media, new AllowAllAccessPolicy());
        var access = new AccessContext("u", MaxClassification: 1, AllowedDivisions: [401]);
        var row = (await catalog.GetAsync(id, access)).ShouldNotBeNull();
        row.FilmstripStoredFileName.ShouldBe("aaaabbbbccccddddaaaabbbbccccdddd.jpg");
        row.FilmstripTileCount.ShouldBe(40);
        row.FilmstripStepMs.ShouldBe(1_000);
        row.RecordedAt.ShouldBe(Recorded);
        (await catalog.ListAsync([id], access)).ShouldHaveSingleItem().FilmstripTileCount.ShouldBe(40);
        (await store.GetForIndexingAsync(id)).ShouldNotBeNull().ExistingFilmstripFileName.ShouldBe("aaaabbbbccccddddaaaabbbbccccdddd.jpg");

        // Переиндексация, лента не собралась: прежняя лента остаётся (снята с того же оригинала).
        await store.CompleteIndexingAsync(id, [], "yunet-2", "sface-2", probe: new VideoProbe(25, TimeSpan.FromSeconds(40), 1280, 720, Recorded));
        (await catalog.GetAsync(id, access)).ShouldNotBeNull().FilmstripStoredFileName.ShouldBe("aaaabbbbccccddddaaaabbbbccccdddd.jpg");

        // Новая проба времени записи не нашла — время снимается (поверх, как частота и размер); дата съёмки не трогается.
        await store.CompleteIndexingAsync(id, [], "yunet-2", "sface-2", probe: new VideoProbe(25, TimeSpan.FromSeconds(40), 1280, 720));
        (await catalog.GetAsync(id, access)).ShouldNotBeNull().RecordedAt.ShouldBeNull();

        // Без пробы (сбой ffprobe) — время записи не стирается оценками.
        await store.CompleteIndexingAsync(id, [], "yunet-2", "sface-2", probe: new VideoProbe(25, TimeSpan.FromSeconds(40), 1280, 720, Recorded));
        await store.CompleteIndexingAsync(id, [], "yunet-2", "sface-2");
        (await catalog.GetAsync(id, access)).ShouldNotBeNull().RecordedAt.ShouldBe(Recorded);
    }

    [Fact(DisplayName = "Файл ленты разрешается только своему носителю, с грифом и подразделением носителя; чужое имя или чужой носитель — не найден")]
    public async Task Filmstrip_file_resolves_only_for_its_asset()
    {
        var store = new MediaStore(fixture.Media, new RecordingFileStorage());
        var video = await store.ReceiveAsync(Draft("clip.mkv", division: 402) with { Classification = 2 }, new MemoryStream([4, 0, 2]));
        var other = await store.ReceiveAsync(Draft("other.mkv", division: 402), new MemoryStream([4, 0, 3]));
        const string strip = "11112222333344441111222233334444.jpg";
        await store.CompleteIndexingAsync(video.AssetId, [], "yunet-1", "sface-1", filmstrip: new FilmstripDraft(strip, 10, 2_000));

        var resolver = new MediaFileAccessResolver(fixture.Media);

        var file = (await resolver.ResolveAsync(MediaFileCategories.Filmstrips, video.AssetId, strip)).ShouldNotBeNull();
        file.ContentType.ShouldBe("image/jpeg");
        file.SubPath.ShouldBe(video.AssetId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        file.Classification.ShouldBe<short>(2);
        file.DivisionId.ShouldBe(402);
        file.CaseRef.ShouldBeNull(); // область дел — по носителю (эндпоинт спросит ICaseScope)

        (await resolver.ResolveAsync(MediaFileCategories.Filmstrips, other.AssetId, strip)).ShouldBeNull();
        (await resolver.ResolveAsync(MediaFileCategories.Filmstrips, video.AssetId, "99999999999999999999999999999999.jpg")).ShouldBeNull();
        // Имя ленты в другой категории не разрешается (вырезки лиц ищутся по лицам).
        (await resolver.ResolveAsync(MediaFileCategories.FaceCrops, video.AssetId, strip)).ShouldBeNull();
    }

    [Fact(DisplayName = "ТБ-064/075: уничтожение носителя снимает и ленту кадров — файл удалён, в акте учтён")]
    public async Task Purge_removes_filmstrip_file()
    {
        var store = new MediaStore(fixture.Media, new RecordingFileStorage());
        var video = await store.ReceiveAsync(Draft("clip.webm", division: 403), new MemoryStream([4, 0, 4]));
        const string strip = "55556666777788885555666677778888.jpg";
        await store.CompleteIndexingAsync(video.AssetId, [], "yunet-1", "sface-1", filmstrip: new FilmstripDraft(strip, 5, 1_000));

        var storage = new RecordingFileStorage();
        var purger = new MediaPurger(fixture.Media, new AuditWriter(fixture.Core), storage);

        var result = await purger.PurgeAsync(video.AssetId, subjectId: 42);

        result.FilesRemoved.ShouldBe(2); // оригинал + лента
        var subPath = video.AssetId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        storage.Deleted.ShouldContain($"{MediaFileCategories.Filmstrips}/{subPath}/{strip}");

        await using var core = await fixture.Core.CreateDbContextAsync();
        var act = await core.AuditRecords.AsNoTracking()
            .Where(e => e.Action == ISC.AI.Abstractions.Audit.AuditAction.Purge && e.ObjectRef == "media:asset:" + subPath)
            .Select(e => e.PayloadSensitive)
            .SingleAsync();
        act.ShouldNotBeNull().ShouldContain("ленты кадров");
    }

    private static MediaAssetDraft Draft(string fileName, int division) => new(
        OriginalFileName: fileName,
        ContentType: "video/mp4",
        Kind: MediaKind.Video,
        Classification: 1,
        DivisionId: division,
        Source: "тест",
        CapturedAt: null,
        UploadedByUserId: 10);
}
