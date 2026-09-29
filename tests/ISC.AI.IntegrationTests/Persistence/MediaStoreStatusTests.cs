using System;
using System.Linq;
using System.Threading.Tasks;
using ISC.AI.Modules.Media.Data;
using ISC.AI.Modules.Media.Data.Entities;
using ISC.AI.Modules.Media.Domain.Model;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Xunit;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Переходы статусов носителя на настоящей БД: восстановление после перезапуска хоста (очередь задач в памяти
/// теряется — «в очереди / в работе» не должно висеть вечно), перевод «видео» без видеопотока в аудиозаписи
/// (ADR-0026) и условная постановка повторной расшифровки (два нажатия не ставят два прогона).
/// </summary>
/// <remarks>Требуется Docker. Свой контейнер (фикстура класса): восстановление — глобальный UPDATE по статусам.</remarks>
[Trait("Category", "Gate")]
public sealed class MediaStoreStatusTests(MediaTranscriptFixture fixture) : IClassFixture<MediaTranscriptFixture>
{
    [Fact(DisplayName = "Старт хоста: расшифровка «в очереди»/«идёт» и индексация «обрабатывается» → «ошибка» с причиной «прервано перезапуском»; готово, ошибка, неприменимо и «загружен» не меняются")]
    public async Task Recover_interrupted_marks_in_flight_work_failed()
    {
        var store = new MediaStore(fixture.Media, new RecordingFileStorage());
        var pending = await SeedAsync(MediaKind.Audio, TranscriptStatus.Pending, MediaIndexStatus.NotApplicable);
        var processing = await SeedAsync(MediaKind.Video, TranscriptStatus.Processing, MediaIndexStatus.Indexed);
        var done = await SeedAsync(MediaKind.Audio, TranscriptStatus.Done, MediaIndexStatus.NotApplicable);
        var failed = await SeedAsync(MediaKind.Audio, TranscriptStatus.Failed, MediaIndexStatus.NotApplicable, transcriptError: "модель не прошла проверку");
        var image = await SeedAsync(MediaKind.Image, TranscriptStatus.NotApplicable, MediaIndexStatus.Indexed);
        var indexing = await SeedAsync(MediaKind.Video, TranscriptStatus.Done, MediaIndexStatus.Processing);

        // «Загружен» — в нём же остаётся носитель, которому конвейер отказал по закрытому делу (ТБ-074): не сбой.
        var uploaded = await SeedAsync(MediaKind.Image, TranscriptStatus.NotApplicable, MediaIndexStatus.Uploaded);
        var indexFailed = await SeedAsync(MediaKind.Image, TranscriptStatus.NotApplicable, MediaIndexStatus.Failed, indexError: "детектор");

        var recovered = await store.RecoverInterruptedAsync();

        recovered.Transcriptions.ShouldBeGreaterThanOrEqualTo(2);
        recovered.Indexings.ShouldBeGreaterThanOrEqualTo(1);

        await using (var db = await fixture.Media.CreateDbContextAsync())
        {
            var rows = await db.Assets.AsNoTracking()
                .Where(a => new[] { pending, processing, done, failed, image, indexing, uploaded, indexFailed }.Contains(a.Id))
                .ToDictionaryAsync(a => a.Id);

            rows[pending].TranscriptStatus.ShouldBe(TranscriptStatus.Failed);
            rows[pending].TranscriptError.ShouldBe(MediaStore.InterruptedTranscriptionReason);
            rows[pending].IndexStatus.ShouldBe(MediaIndexStatus.NotApplicable);
            rows[processing].TranscriptStatus.ShouldBe(TranscriptStatus.Failed);
            rows[processing].TranscriptError.ShouldBe(MediaStore.InterruptedTranscriptionReason);
            rows[processing].IndexStatus.ShouldBe(MediaIndexStatus.Indexed);

            rows[indexing].IndexStatus.ShouldBe(MediaIndexStatus.Failed);
            rows[indexing].IndexError.ShouldBe(MediaStore.InterruptedIndexingReason);
            rows[indexing].TranscriptStatus.ShouldBe(TranscriptStatus.Done);

            rows[done].TranscriptStatus.ShouldBe(TranscriptStatus.Done);
            rows[done].TranscriptError.ShouldBeNull();
            rows[failed].TranscriptStatus.ShouldBe(TranscriptStatus.Failed);
            rows[failed].TranscriptError.ShouldBe("модель не прошла проверку");
            rows[image].TranscriptStatus.ShouldBe(TranscriptStatus.NotApplicable);
            rows[image].IndexStatus.ShouldBe(MediaIndexStatus.Indexed);
            rows[uploaded].IndexStatus.ShouldBe(MediaIndexStatus.Uploaded);
            rows[uploaded].IndexError.ShouldBeNull();
            rows[indexFailed].IndexError.ShouldBe("детектор");
        }

        // Тексты причин ведут к действию в карточке.
        MediaStore.InterruptedTranscriptionReason.ShouldContain("Расшифровать заново");
        MediaStore.InterruptedIndexingReason.ShouldContain("Переиндексировать");

        // Повтор (следующий старт) — ничего не находит: всё прерванное уже переведено.
        (await store.RecoverInterruptedAsync()).IsEmpty.ShouldBeTrue();
    }

    [Fact(DisplayName = "ADR-0026: «видео» без видеопотока → аудиозапись, лица «неприменимо», расшифровка не тронута; изображение и уже аудио этим путём не меняются")]
    public async Task Reclassify_turns_soundless_video_into_audio()
    {
        var store = new MediaStore(fixture.Media, new RecordingFileStorage());
        var voice = await SeedAsync(MediaKind.Video, TranscriptStatus.Done, MediaIndexStatus.Uploaded, contentType: "video/3gpp");
        var image = await SeedAsync(MediaKind.Image, TranscriptStatus.NotApplicable, MediaIndexStatus.Uploaded);

        (await store.ReclassifyAsAudioAsync(voice)).ShouldBeTrue();
        (await store.ReclassifyAsAudioAsync(voice)).ShouldBeFalse(); // уже аудио — повтор ничего не меняет
        (await store.ReclassifyAsAudioAsync(image)).ShouldBeFalse();
        (await store.ReclassifyAsAudioAsync(999_999)).ShouldBeFalse();

        await using var db = await fixture.Media.CreateDbContextAsync();
        var row = await db.Assets.AsNoTracking().SingleAsync(a => a.Id == voice);
        row.Kind.ShouldBe(MediaKind.Audio);
        row.IndexStatus.ShouldBe(MediaIndexStatus.NotApplicable);
        row.IndexError.ShouldBeNull();
        row.TranscriptStatus.ShouldBe(TranscriptStatus.Done);
        row.ContentType.ShouldBe("video/3gpp"); // заявленный тип файла не подменяется

        var untouched = await db.Assets.AsNoTracking().SingleAsync(a => a.Id == image);
        untouched.Kind.ShouldBe(MediaKind.Image);
        untouched.IndexStatus.ShouldBe(MediaIndexStatus.Uploaded);
    }

    [Fact(DisplayName = "Повторная расшифровка: постановка условная — из «готово», «ошибка», «неприменимо» можно (причина снимается), из «в очереди»/«идёт» нельзя; носителя нет — нельзя")]
    public async Task Try_mark_pending_is_conditional()
    {
        var store = new MediaStore(fixture.Media, new RecordingFileStorage());
        var done = await SeedAsync(MediaKind.Audio, TranscriptStatus.Done, MediaIndexStatus.NotApplicable);
        var failed = await SeedAsync(MediaKind.Audio, TranscriptStatus.Failed, MediaIndexStatus.NotApplicable, transcriptError: "сбой");
        var legacyVideo = await SeedAsync(MediaKind.Video, TranscriptStatus.NotApplicable, MediaIndexStatus.Indexed);
        var pending = await SeedAsync(MediaKind.Audio, TranscriptStatus.Pending, MediaIndexStatus.NotApplicable);
        var processing = await SeedAsync(MediaKind.Audio, TranscriptStatus.Processing, MediaIndexStatus.NotApplicable);

        (await store.TryMarkTranscriptionPendingAsync(done)).ShouldBeTrue();
        (await store.TryMarkTranscriptionPendingAsync(failed)).ShouldBeTrue();
        (await store.TryMarkTranscriptionPendingAsync(legacyVideo)).ShouldBeTrue();
        (await store.TryMarkTranscriptionPendingAsync(pending)).ShouldBeFalse();
        (await store.TryMarkTranscriptionPendingAsync(processing)).ShouldBeFalse();
        (await store.TryMarkTranscriptionPendingAsync(999_999)).ShouldBeFalse();

        // Второе нажатие на только что поставленный — уже «в очереди».
        (await store.TryMarkTranscriptionPendingAsync(done)).ShouldBeFalse();

        await using var db = await fixture.Media.CreateDbContextAsync();
        var rows = await db.Assets.AsNoTracking()
            .Where(a => new[] { done, failed, legacyVideo, pending, processing }.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id);
        rows[done].TranscriptStatus.ShouldBe(TranscriptStatus.Pending);
        rows[failed].TranscriptStatus.ShouldBe(TranscriptStatus.Pending);
        rows[failed].TranscriptError.ShouldBeNull();
        rows[legacyVideo].TranscriptStatus.ShouldBe(TranscriptStatus.Pending);
        rows[processing].TranscriptStatus.ShouldBe(TranscriptStatus.Processing); // идущий прогон не сбит в «очередь»
    }

    [Fact(DisplayName = "Параллельные нажатия «Расшифровать заново»: условный UPDATE пропускает ровно одно")]
    public async Task Parallel_try_mark_pending_lets_exactly_one_through()
    {
        var store = new MediaStore(fixture.Media, new RecordingFileStorage());
        var asset = await SeedAsync(MediaKind.Audio, TranscriptStatus.Done, MediaIndexStatus.NotApplicable);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => store.TryMarkTranscriptionPendingAsync(asset))));

        results.Count(accepted => accepted).ShouldBe(1);
    }

    [Fact(DisplayName = "ТФ-МЕД-17: дата съёмки записывается в UTC и перезаписывается исправлением; носителя нет — false")]
    public async Task Captured_at_is_stored_in_utc()
    {
        var store = new MediaStore(fixture.Media, new RecordingFileStorage());
        var photo = await SeedAsync(MediaKind.Image, TranscriptStatus.NotApplicable, MediaIndexStatus.Uploaded);
        var local = new DateTimeOffset(2026, 9, 28, 14, 30, 0, TimeSpan.FromHours(6));

        (await store.SetCapturedAtAsync(photo, local)).ShouldBeTrue();
        (await store.SetCapturedAtAsync(999_999, local)).ShouldBeFalse();

        await using (var db = await fixture.Media.CreateDbContextAsync())
        {
            var stored = (await db.Assets.AsNoTracking().SingleAsync(a => a.Id == photo)).CapturedAt.ShouldNotBeNull();
            stored.Offset.ShouldBe(TimeSpan.Zero);
            stored.ShouldBe(local); // тот же момент времени
        }

        (await store.SetCapturedAtAsync(photo, local.AddHours(1))).ShouldBeTrue();
        await using (var db = await fixture.Media.CreateDbContextAsync())
        {
            (await db.Assets.AsNoTracking().SingleAsync(a => a.Id == photo)).CapturedAt.ShouldBe(local.AddHours(1));
        }
    }

    private async Task<int> SeedAsync(
        MediaKind kind,
        TranscriptStatus transcriptStatus,
        MediaIndexStatus indexStatus,
        string? transcriptError = null,
        string? indexError = null,
        string? contentType = null)
    {
        await using var db = await fixture.Media.CreateDbContextAsync();
        var stored = Guid.NewGuid().ToString("N") + (kind == MediaKind.Image ? ".jpg" : ".mp4");
        var asset = new MediaAsset
        {
            Kind = kind,
            OriginalFileName = stored,
            StoredFileName = stored,
            ContentType = contentType ?? (kind == MediaKind.Image ? "image/jpeg" : kind == MediaKind.Video ? "video/mp4" : "audio/ogg"),
            ContentHash = Guid.NewGuid().ToString("N"),
            ByteSize = 1,
            Classification = 1,
            DivisionId = 201,
            IndexStatus = indexStatus,
            IndexError = indexError,
            TranscriptStatus = transcriptStatus,
            TranscriptError = transcriptError,
        };
        db.Assets.Add(asset);
        await db.SaveChangesAsync();
        return asset.Id;
    }
}
