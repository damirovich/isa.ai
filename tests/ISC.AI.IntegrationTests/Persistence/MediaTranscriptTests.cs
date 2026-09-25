using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Data;
using ISC.AI.Modules.Media.Data.Entities;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Persistence;
using ISC.AI.Persistence.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Shouldly;
using Testcontainers.PostgreSql;
using Xunit;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Общий контейнер Postgres+pgvector для тестов расшифровки: схемы <c>media</c> и <c>core</c> накатываются один
/// раз. Тесты класса не мешают друг другу — у каждого свои подразделения (решётка режет по ним).
/// </summary>
public sealed class MediaTranscriptFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = TestPostgres.Create();

    /// <summary>Строка подключения к контейнеру.</summary>
    public string ConnectionString => _postgres.GetConnectionString();

    /// <summary>Фабрика контекста схемы <c>media</c>.</summary>
    public IDbContextFactory<MediaDbContext> Media { get; private set; } = null!;

    /// <summary>Фабрика контекста схемы <c>core</c> (журнал аудита).</summary>
    public IDbContextFactory<CoreDbContext> Core { get; private set; } = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        Media = new MediaContextFactory(ConnectionString);
        Core = new CoreContextFactory(ConnectionString);

        await using (var db = await Media.CreateDbContextAsync())
        {
            await db.Database.MigrateAsync();
        }

        await using (var db = await Core.CreateDbContextAsync())
        {
            await db.Database.MigrateAsync();
        }
    }

    /// <inheritdoc />
    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();
}

/// <summary>
/// Расшифровка речи на настоящей БД (ADR-0026): фрагменты пишутся и ПЕРЕЗАПИСЫВАЮТСЯ ЦЕЛИКОМ одной транзакцией с
/// грифом носителя; решётка (ТБ-020/021) — на стороне БД и для чтения, и для поиска, в т.ч. по строке фрагмента;
/// поиск — подстрока без учёта регистра с киргизскими буквами, только в перечисленных носителях, метасимволы LIKE
/// буквальны; уничтожение носителя сносит расшифровку каскадом (ТБ-064, ADR-0025); миграция помечает старые
/// носители «неприменимо».
/// </summary>
/// <remarks>Требуется Docker.</remarks>
[Trait("Category", "Gate")]
public sealed class MediaTranscriptTests(MediaTranscriptFixture fixture) : IClassFixture<MediaTranscriptFixture>
{
    [Fact(DisplayName = "Приём (ADR-0026): аудио — лица «неприменимо», расшифровка «в очереди»; видео — оба в очереди; фото — расшифровка «неприменимо»")]
    public async Task Receive_sets_statuses_by_kind()
    {
        var store = new MediaStore(fixture.Media, new RecordingFileStorage());

        var audio = await store.ReceiveAsync(Draft("voice.ogg", "audio/ogg", MediaKind.Audio, division: 101), new MemoryStream([1, 1, 1]));
        var video = await store.ReceiveAsync(Draft("clip.mp4", "video/mp4", MediaKind.Video, division: 101), new MemoryStream([2, 2, 2]));
        var image = await store.ReceiveAsync(Draft("photo.jpg", "image/jpeg", MediaKind.Image, division: 101), new MemoryStream([3, 3, 3]));

        await using var db = await fixture.Media.CreateDbContextAsync();
        var rows = await db.Assets.AsNoTracking()
            .Where(a => a.DivisionId == 101)
            .ToDictionaryAsync(a => a.Id, a => (a.IndexStatus, a.TranscriptStatus));

        rows[audio.AssetId].ShouldBe((MediaIndexStatus.NotApplicable, TranscriptStatus.Pending));
        rows[video.AssetId].ShouldBe((MediaIndexStatus.Uploaded, TranscriptStatus.Pending));
        rows[image.AssetId].ShouldBe((MediaIndexStatus.Uploaded, TranscriptStatus.NotApplicable));

        // Метаданные для конвейера — гриф и подразделение носителя, без контекста доступа.
        var info = await store.GetForTranscriptionAsync(audio.AssetId);
        info.ShouldBe(new MediaAssetTranscriptionInfo(audio.AssetId, MediaKind.Audio, info!.StoredFileName, 1, 101));
        (await store.GetForTranscriptionAsync(999_999)).ShouldBeNull();
    }

    [Fact(DisplayName = "Фрагменты пишутся с грифом носителя и перезаписываются ЦЕЛИКОМ; сбой и постановка в очередь прежнюю расшифровку не трогают")]
    public async Task Segments_are_written_and_replaced_whole()
    {
        var store = new MediaStore(fixture.Media, new RecordingFileStorage());
        var audio = await SeedAssetAsync(MediaKind.Audio, classification: 2, division: 102, durationMs: null);
        var video = await SeedAssetAsync(MediaKind.Video, classification: 2, division: 102, durationMs: 12_000);
        var neighbour = await SeedAssetAsync(MediaKind.Audio, classification: 2, division: 102, durationMs: null);
        await store.CompleteTranscriptionAsync(neighbour, [Segment(0, 0, 500, "соседний носитель")], "m1");

        await store.MarkTranscriptionProcessingAsync(audio);
        (await StatusAsync(audio)).Status.ShouldBe(TranscriptStatus.Processing);

        await store.CompleteTranscriptionAsync(
            audio,
            [Segment(0, 400, 2100, "салам алейкум"), Segment(1, 2600, 5200, "мен үйдөмүн"), Segment(2, 6000, 9000, "ну давай")],
            "gigaam-v1@ab12",
            durationMs: 9000);

        await using (var db = await fixture.Media.CreateDbContextAsync())
        {
            var segments = await db.TranscriptSegments.AsNoTracking().Where(s => s.AssetId == audio).OrderBy(s => s.Index).ToListAsync();
            segments.Select(s => s.Text).ShouldBe(["салам алейкум", "мен үйдөмүн", "ну давай"]);
            segments.ShouldAllBe(s => s.Classification == 2 && s.DivisionId == 102 && s.ModelVersion == "gigaam-v1@ab12");
            segments[1].StartMs.ShouldBe(2600);
            segments[1].EndMs.ShouldBe(5200);

            var asset = await db.Assets.AsNoTracking().SingleAsync(a => a.Id == audio);
            asset.TranscriptStatus.ShouldBe(TranscriptStatus.Done);
            asset.TranscriberVersion.ShouldBe("gigaam-v1@ab12");
            asset.TranscribedAt.ShouldNotBeNull();
            asset.TranscriptError.ShouldBeNull();
            asset.DurationMs.ShouldBe(9000); // у аудио длительности не было — берётся от распознавателя
        }

        // Повторная расшифровка — ЦЕЛИКОМ: ни одного фрагмента прежнего прогона не остаётся.
        await store.CompleteTranscriptionAsync(audio, [Segment(0, 100, 800, "другая модель")], "vosk-0.42", durationMs: 5000);
        var second = await SegmentsAsync(audio);
        second.ShouldHaveSingleItem().Text.ShouldBe("другая модель");
        (await StatusAsync(audio)).Version.ShouldBe("vosk-0.42");
        (await StatusAsync(audio)).DurationMs.ShouldBe(5000); // у аудио — длительность записи последнего прогона

        // Постановка в очередь и неудача прежнюю расшифровку НЕ снимают (её заменяет только успешный прогон).
        (await store.TryMarkTranscriptionPendingAsync(audio)).ShouldBeTrue();
        (await StatusAsync(audio)).Status.ShouldBe(TranscriptStatus.Pending);
        await store.FailTranscriptionAsync(audio, new string('x', 2500));
        var failed = await StatusAsync(audio);
        failed.Status.ShouldBe(TranscriptStatus.Failed);
        failed.Error!.Length.ShouldBe(2000); // причина обрезается под столбец
        (await SegmentsAsync(audio)).ShouldHaveSingleItem();

        // Испорченный выход распознавателя (повтор номера) не пишется вовсе — ни частично, ни статусом «готово».
        await Should.ThrowAsync<ArgumentException>(() => store.CompleteTranscriptionAsync(
            audio, [Segment(0, 0, 100, "а"), Segment(0, 200, 300, "б")], "m3"));
        (await SegmentsAsync(audio)).ShouldHaveSingleItem().Text.ShouldBe("другая модель");
        (await StatusAsync(audio)).Status.ShouldBe(TranscriptStatus.Failed);

        // Речи нет — расшифровка готова и пуста.
        await store.CompleteTranscriptionAsync(audio, [], "m4");
        (await SegmentsAsync(audio)).ShouldBeEmpty();
        (await StatusAsync(audio)).Status.ShouldBe(TranscriptStatus.Done);

        // Видео: длительность дала раскадровка — оценка распознавателя её не меняет.
        await store.CompleteTranscriptionAsync(video, [Segment(0, 0, 3000, "стой")], "m1", durationMs: 3000);
        (await StatusAsync(video)).DurationMs.ShouldBe(12_000);

        // Соседний носитель не затронут ни одним прогоном.
        (await SegmentsAsync(neighbour)).ShouldHaveSingleItem().Text.ShouldBe("соседний носитель");
    }

    [Fact(DisplayName = "Длительность: у аудио длительность записи от распознавателя пишется всегда (поверх прежней оценки, и при нуле фрагментов); у видео значение раскадровки не перетирается")]
    public async Task Duration_is_overwritten_for_audio_and_kept_for_video()
    {
        var store = new MediaStore(fixture.Media, new RecordingFileStorage());

        // Аудио с прежней оценкой «до конца последней речи» (12:30) — повторная расшифровка исправляет её на длину
        // записи (45:00), даже если речи в записи не нашлось.
        var audio = await SeedAssetAsync(MediaKind.Audio, classification: 1, division: 107, durationMs: 750_000);
        await store.CompleteTranscriptionAsync(audio, [], "m1", durationMs: 2_700_000);
        (await StatusAsync(audio)).DurationMs.ShouldBe(2_700_000);

        // Длительность неизвестна (звуковой дорожки нет) — прежнее значение остаётся.
        await store.CompleteTranscriptionAsync(audio, [], "m1", durationMs: null);
        (await StatusAsync(audio)).DurationMs.ShouldBe(2_700_000);

        // Видео: длительность дала раскадровка — значение распознавателя её не меняет.
        var video = await SeedAssetAsync(MediaKind.Video, classification: 1, division: 107, durationMs: 60_000);
        await store.CompleteTranscriptionAsync(video, [Segment(0, 0, 900, "стой")], "m1", durationMs: 61_500);
        (await StatusAsync(video)).DurationMs.ShouldBe(60_000);

        // Видео без длительности (раскадровка не удалась или отказала по закрытому делу) — берётся от распознавателя.
        var unknownVideo = await SeedAssetAsync(MediaKind.Video, classification: 1, division: 107, durationMs: null);
        await store.CompleteTranscriptionAsync(unknownVideo, [], "m1", durationMs: 30_000);
        (await StatusAsync(unknownVideo)).DurationMs.ShouldBe(30_000);
    }

    [Fact(DisplayName = "ТБ-020/021: фрагменты носителя выше допуска или чужого подразделения не выдаются ни чтением, ни поиском; без контекста — отказ")]
    public async Task Lattice_hides_transcripts_in_read_and_search()
    {
        var store = new MediaStore(fixture.Media, new RecordingFileStorage());
        var open = await SeedAssetAsync(MediaKind.Audio, classification: 1, division: 103);
        var secret = await SeedAssetAsync(MediaKind.Audio, classification: 3, division: 103);
        var foreign = await SeedAssetAsync(MediaKind.Audio, classification: 0, division: 104);
        await store.CompleteTranscriptionAsync(open, [Segment(0, 0, 900, "салам досум")], "m1");
        await store.CompleteTranscriptionAsync(secret, [Segment(0, 0, 900, "салам секрет")], "m1");
        await store.CompleteTranscriptionAsync(foreign, [Segment(0, 0, 900, "салам чужой отдел")], "m1");

        // Аномалия, которой хранилище не создаёт, — фрагмент с грифом ВЫШЕ носителя: решётка режет по строке
        // фрагмента, а не только по носителю.
        await using (var db = await fixture.Media.CreateDbContextAsync())
        {
            db.TranscriptSegments.Add(new TranscriptSegment
            {
                AssetId = open, Index = 1, StartMs = 1000, EndMs = 1900, Text = "салам сверх допуска",
                ModelVersion = "m1", Classification = 3, DivisionId = 103,
            });
            await db.SaveChangesAsync();
        }

        var catalog = new MediaCatalog(fixture.Media, new AllowAllAccessPolicy());
        var access = new AccessContext("u1", MaxClassification: 1, AllowedDivisions: [103]);

        var visible = await catalog.GetTranscriptAsync(open, access);
        visible.ShouldNotBeNull().Status.ShouldBe(TranscriptStatus.Done);
        visible.ModelVersion.ShouldBe("m1");
        visible.Segments.ShouldHaveSingleItem().Text.ShouldBe("салам досум");
        (await catalog.GetTranscriptAsync(secret, access)).ShouldBeNull();
        (await catalog.GetTranscriptAsync(foreign, access)).ShouldBeNull();
        (await catalog.GetTranscriptAsync(999_999, access)).ShouldBeNull();

        var hits = await catalog.SearchTranscriptsAsync([open, secret, foreign], "салам", 50, access);
        hits.ShouldHaveSingleItem().Text.ShouldBe("салам досум");

        // Повышенный допуск открывает секретный носитель того же подразделения, но не чужое подразделение.
        var cleared = access with { MaxClassification = 3 };
        (await catalog.GetTranscriptAsync(secret, cleared)).ShouldNotBeNull().Segments.ShouldHaveSingleItem();
        (await catalog.GetTranscriptAsync(open, cleared)).ShouldNotBeNull().Segments.Count.ShouldBe(2);
        var clearedHits = await catalog.SearchTranscriptsAsync([open, secret, foreign], "салам", 50, cleared);
        clearedHits.Select(h => h.AssetId).ShouldBe([open, open, secret]);
        clearedHits.ShouldAllBe(h => h.AssetId != foreign);

        // Состояние расшифровки видно и в строке носителя (медиатека дела, карточка) — без отдельного запроса.
        (await catalog.ListAsync([open], access)).ShouldHaveSingleItem().TranscriptStatus.ShouldBe(TranscriptStatus.Done);

        // Fail-closed (ТБ-021): без контекста — исключение, а не выдача без фильтра; даже при пустой области.
        await Should.ThrowAsync<AccessContextRequiredException>(() => catalog.GetTranscriptAsync(open, null!));
        await Should.ThrowAsync<AccessContextRequiredException>(() => catalog.SearchTranscriptsAsync([open], "салам", 50, null!));
        await Should.ThrowAsync<AccessContextRequiredException>(() => catalog.SearchTranscriptsAsync([], "салам", 50, null!));
    }

    [Fact(DisplayName = "Поиск: подстрока без учёта регистра (ү/ө/ң), только перечисленные носители, порядок — носитель и таймкод, предел, % и _ буквальны")]
    public async Task Search_matches_substring_case_insensitively_within_listed_assets()
    {
        var store = new MediaStore(fixture.Media, new RecordingFileStorage());
        var first = await SeedAssetAsync(MediaKind.Audio, classification: 0, division: 105, fileName: "допрос-1.m4a");
        var second = await SeedAssetAsync(MediaKind.Video, classification: 0, division: 105, fileName: "камера-2.mp4");
        var notListed = await SeedAssetAsync(MediaKind.Audio, classification: 0, division: 105);

        await store.CompleteTranscriptionAsync(first,
        [
            Segment(0, 0, 1000, "мен үйдө отурам"),
            Segment(1, 5000, 6000, "үйгө бар"),
            Segment(2, 2000, 3000, "өзүң кел бишкекке бар"),
        ], "m1");
        await store.CompleteTranscriptionAsync(second,
        [
            Segment(0, 100, 900, "биз үйдөбүз"),
            Segment(1, 1000, 1900, "скидка 50% и под_черк"),
            Segment(2, 2000, 2900, "задача решена"),
            Segment(3, 3000, 3900, "Бишкек шаары"),
        ], "m1");
        await store.CompleteTranscriptionAsync(notListed, [Segment(0, 0, 900, "үй бүлө")], "m1");

        var catalog = new MediaCatalog(fixture.Media, new AllowAllAccessPolicy());
        var access = new AccessContext("u1", 0, [105]);
        int[] scope = [first, second];

        // Основа находит слова с аффиксами; заглавные в запросе — те же буквы; неперечисленный носитель — вне выдачи.
        var home = await catalog.SearchTranscriptsAsync(scope, "ҮЙ", 50, access);
        home.Select(h => (h.AssetId, h.SegmentIndex)).ShouldBe([(first, 0), (first, 1), (second, 0)]);
        home[0].AssetFileName.ShouldBe("допрос-1.m4a");
        home[0].Kind.ShouldBe(MediaKind.Audio);
        home[0].StartMs.ShouldBe(0);
        home[1].StartMs.ShouldBe(5000);
        home[2].Kind.ShouldBe(MediaKind.Video);
        home.ShouldAllBe(h => h.AssetId != notListed);

        // Порядок внутри носителя — по таймкоду, а не по номеру: №2 (2000 мс) раньше №1 (5000 мс).
        (await catalog.SearchTranscriptsAsync([first], "бар", 50, access)).Select(h => h.SegmentIndex).ShouldBe([2, 1]);

        // Все три киргизские буквы (ө, ү, ң) — в любом регистре запроса.
        (await catalog.SearchTranscriptsAsync([first], "Өзүң КЕЛ", 50, access)).ShouldHaveSingleItem().SegmentIndex.ShouldBe(2);

        // Короче двух символов (в т.ч. после обрезки пробелов) — выдачи нет: совпало бы почти всё.
        (await catalog.SearchTranscriptsAsync([first], "ү", 50, access)).ShouldBeEmpty();
        (await catalog.SearchTranscriptsAsync([first], "  ө  ", 50, access)).ShouldBeEmpty();

        // Текст, записанный с заглавной, находится строчными (регистронезависимость ILIKE — по локали БД; модель
        // сама пишет строчными, см. MediaCatalog.SearchTranscriptsAsync).
        (await catalog.SearchTranscriptsAsync(scope, "бишкек", 50, access)).Select(h => (h.AssetId, h.SegmentIndex))
            .ShouldBe([(first, 2), (second, 3)]);

        // Предел выдачи.
        (await catalog.SearchTranscriptsAsync(scope, "үй", 2, access)).Select(h => (h.AssetId, h.SegmentIndex))
            .ShouldBe([(first, 0), (first, 1)]);

        // Метасимволы LIKE — буквальны: «0%» не «0 и что угодно», «д_ч» не совпадает с «дач» в «задача».
        (await catalog.SearchTranscriptsAsync(scope, "0%", 50, access)).ShouldHaveSingleItem().SegmentIndex.ShouldBe(1);
        (await catalog.SearchTranscriptsAsync(scope, "д_ч", 50, access)).ShouldHaveSingleItem().Text.ShouldBe("скидка 50% и под_черк");
        (await catalog.SearchTranscriptsAsync(scope, "%%", 50, access)).ShouldBeEmpty();

        // Пустая область и пустой текст — пусто, без запроса «по всему».
        (await catalog.SearchTranscriptsAsync([], "үй", 50, access)).ShouldBeEmpty();
        (await catalog.SearchTranscriptsAsync(scope, "   ", 50, access)).ShouldBeEmpty();
    }

    [Fact(DisplayName = "ТБ-064/ADR-0025: уничтожение носителя сносит его расшифровку каскадом и называет её объём в акте; соседняя цела")]
    public async Task Purge_removes_transcript_by_cascade()
    {
        var store = new MediaStore(fixture.Media, new RecordingFileStorage());
        var target = await SeedAssetAsync(MediaKind.Audio, classification: 2, division: 106);
        var other = await SeedAssetAsync(MediaKind.Audio, classification: 2, division: 106);
        await store.CompleteTranscriptionAsync(target, [Segment(0, 0, 900, "уничтожить"), Segment(1, 1000, 1900, "всё")], "m1");
        await store.CompleteTranscriptionAsync(other, [Segment(0, 0, 900, "оставить")], "m1");

        var purger = new MediaPurger(fixture.Media, new AuditWriter(fixture.Core), new RecordingFileStorage());
        var result = await purger.PurgeAsync(target, subjectId: 42);

        result.Found.ShouldBeTrue();
        await using (var db = await fixture.Media.CreateDbContextAsync())
        {
            (await db.Assets.AnyAsync(a => a.Id == target)).ShouldBeFalse();
            (await db.TranscriptSegments.AnyAsync(s => s.AssetId == target)).ShouldBeFalse();
            (await db.TranscriptSegments.CountAsync(s => s.AssetId == other)).ShouldBe(1);
        }

        await using (var db = await fixture.Core.CreateDbContextAsync())
        {
            var objectRef = "media:asset:" + target;
            var audit = await db.AuditRecords.AsNoTracking().SingleAsync(r => r.Action == AuditAction.Purge && r.ObjectRef == objectRef);
            audit.PayloadSensitive.ShouldNotBeNull().ShouldContain("фрагментов расшифровки 2");
            audit.Classification.ShouldBe<short>(2);
            audit.DivisionId.ShouldBe(106);
        }
    }

    [Fact(DisplayName = "Миграция AudioTranscripts: носители, загруженные до неё (фото и видео), получают «расшифровка неприменима»")]
    public async Task Migration_marks_existing_assets_not_applicable()
    {
        // Отдельная база в том же контейнере: здесь нужна схема ДО миграции.
        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = "iscai_transcript_migration" }.ConnectionString;
        var factory = new MediaContextFactory(connectionString);

        await using (var db = factory.CreateDbContext())
        {
            await db.GetService<IMigrator>().MigrateAsync("20260918055616_SearchSessionProbeAndDedup");
            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO media.asset (kind, original_file_name, stored_file_name, content_type, content_hash, byte_size, "
                + "classification, division_id, index_status, created_at) VALUES "
                + "(1, 'a.jpg', 'a.jpg', 'image/jpeg', 'h1', 1, 0, 1, 2, now()), "
                + "(2, 'v.mp4', 'v.mp4', 'video/mp4', 'h2', 1, 0, 1, 2, now())");

            await db.Database.MigrateAsync();
        }

        await using (var db = factory.CreateDbContext())
        {
            var statuses = await db.Assets.AsNoTracking().OrderBy(a => a.Id).Select(a => new { a.Kind, a.TranscriptStatus }).ToListAsync();
            statuses.Count.ShouldBe(2);
            statuses.ShouldAllBe(s => s.TranscriptStatus == TranscriptStatus.NotApplicable);
            (await db.TranscriptSegments.CountAsync()).ShouldBe(0);
        }
    }

    private static TranscriptSegmentDraft Segment(int index, long startMs, long endMs, string text) => new(index, startMs, endMs, text);

    private static MediaAssetDraft Draft(string name, string contentType, MediaKind kind, int division) =>
        new(name, contentType, kind, Classification: 1, DivisionId: division);

    private async Task<int> SeedAssetAsync(
        MediaKind kind, short classification, int division, long? durationMs = null, string? fileName = null)
    {
        await using var db = await fixture.Media.CreateDbContextAsync();
        var stored = Guid.NewGuid().ToString("N") + (kind == MediaKind.Video ? ".mp4" : ".ogg");
        var asset = new MediaAsset
        {
            Kind = kind,
            OriginalFileName = fileName ?? stored,
            StoredFileName = stored,
            ContentType = kind == MediaKind.Video ? "video/mp4" : "audio/ogg",
            ContentHash = Guid.NewGuid().ToString("N"),
            ByteSize = 1,
            DurationMs = durationMs,
            Classification = classification,
            DivisionId = division,
            IndexStatus = kind == MediaKind.Audio ? MediaIndexStatus.NotApplicable : MediaIndexStatus.Indexed,
            TranscriptStatus = TranscriptStatus.Pending,
        };
        db.Assets.Add(asset);
        await db.SaveChangesAsync();
        return asset.Id;
    }

    private async Task<List<TranscriptSegment>> SegmentsAsync(int assetId)
    {
        await using var db = await fixture.Media.CreateDbContextAsync();
        return await db.TranscriptSegments.AsNoTracking().Where(s => s.AssetId == assetId).OrderBy(s => s.Index).ToListAsync();
    }

    private async Task<(TranscriptStatus Status, string? Error, string? Version, long? DurationMs)> StatusAsync(int assetId)
    {
        await using var db = await fixture.Media.CreateDbContextAsync();
        var asset = await db.Assets.AsNoTracking().SingleAsync(a => a.Id == assetId);
        return (asset.TranscriptStatus, asset.TranscriptError, asset.TranscriberVersion, asset.DurationMs);
    }
}
