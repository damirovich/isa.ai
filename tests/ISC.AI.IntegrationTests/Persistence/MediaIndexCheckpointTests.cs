using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Storage;
using ISC.AI.Modules.Media.Application;
using ISC.AI.Modules.Media.Application.Features.Indexing;
using ISC.AI.Modules.Media.Application.Features.Maintenance;
using ISC.AI.Modules.Media.Data;
using ISC.AI.Modules.Media.Data.Entities;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using ISC.AI.Persistence.Audit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Возобновляемая индексация видео (ТНД-007, ADR-0064) на настоящем PostgreSQL: части прогона пишутся и читаются без
/// потерь, дублей кадров не допускают, снимаются публикацией, уничтожением носителя (ТБ-064) и регламентом закрытого дела
/// (ТБ-074); сбой на середине видео не отправляет повтор к началу.
/// </summary>
/// <remarks>
/// Требуется Docker. Свой контейнер (фикстура класса); проверки списков — по своим носителям, не по всей таблице.
/// </remarks>
[Trait("Category", "Gate")]
public sealed class MediaIndexCheckpointTests(MediaTranscriptFixture fixture) : IClassFixture<MediaTranscriptFixture>
{
    private const string Key = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string OtherKey = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";

    /// <summary>Кадры, которые повтор распознаёт после сбоя на кадре 7 (остальные — из частей).</summary>
    private static readonly int[] RemainingFrames = [7, 8, 9];

    [Fact(DisplayName = "ADR-0064: части прогона читаются без потерь (шаблон до бита, точки, качество, оценки кадра с NaN); чужой ключ — null; перекрытие, чужой прогон и уничтоженный носитель — отказ; снятие идемпотентно")]
    public async Task Checkpoint_parts_round_trip_and_reject_duplicates()
    {
        var store = new MediaStore(fixture.Media, new RecordingFileStorage());
        var asset = await SeedAsync(MediaKind.Video, MediaIndexStatus.Processing);

        var template = Enumerable.Range(0, FaceTemplate.Dimensions).Select(i => MathF.Sin(i) / 7f).ToArray();
        template[3] = float.Epsilon;
        template[4] = -0f;
        var landmarks = new FaceLandmarks(new(1.5f, 2.25f), new(3, 4), new(5, 6), new(7, 8), new(9.125f, 10));
        var first = new IndexedFace(
            2, 2000, new DetectedFace(new BoundingBox(10.5f, 20, 30, 40), landmarks, 0.875f),
            new FaceQuality(0.9f, true, null), template, "crop-a.jpg", TrackId: 4,
            new FrameMetrics(float.NaN, 0.5f, 10, -5, 2, 64, 0.75f));
        var second = new IndexedFace(
            3, 3000, new DetectedFace(new BoundingBox(1, 2, 3, 4), default, 0.4f),
            new FaceQuality(0.1f, false, "мало пикселей"), Template: null);

        await store.SaveIndexCheckpointAsync(asset, Key, 0, 3, [first, second]);
        await store.SaveIndexCheckpointAsync(asset, Key, 4, 6, []); // кадры без лиц тоже двигают отметку

        var loaded = (await store.LoadIndexCheckpointAsync(asset, Key)).ShouldNotBeNull();
        loaded.LastFrameIndex.ShouldBe(6);
        loaded.Faces.Count.ShouldBe(2);

        var a = loaded.Faces[0];
        a.FrameIndex.ShouldBe(2);
        a.FrameTimestampMs.ShouldBe(2000);
        a.Face.ShouldBe(first.Face);
        a.Quality.ShouldBe(first.Quality);
        a.Template.ShouldNotBeNull().Select(BitConverter.SingleToInt32Bits)
            .ShouldBe(template.Select(BitConverter.SingleToInt32Bits).ToArray());
        a.CropStoredFileName.ShouldBeNull(); // имён вырезок в частях нет (ADR-0064 п. 6)
        a.TrackId.ShouldBeNull();            // треки назначаются по всем лицам в конце
        var metrics = a.Metrics.ShouldNotBeNull();
        float.IsNaN(metrics.Sharpness).ShouldBeTrue();
        metrics.Score.ShouldBe(0.75f);

        var b = loaded.Faces[1];
        b.Template.ShouldBeNull();
        b.Quality.ShouldBe(second.Quality);
        b.Metrics.ShouldBeNull();

        (await store.LoadIndexCheckpointAsync(asset, OtherKey)).ShouldBeNull();

        // Кадр 6 уже лежит в части; другой прогон того же носителя; носителя нет — всё отказ, дублей не бывает.
        await Should.ThrowAsync<InvalidOperationException>(() => store.SaveIndexCheckpointAsync(asset, Key, 6, 8, []));
        await Should.ThrowAsync<InvalidOperationException>(() => store.SaveIndexCheckpointAsync(asset, OtherKey, 7, 8, []));
        await Should.ThrowAsync<InvalidOperationException>(() => store.SaveIndexCheckpointAsync(999_999, Key, 0, 1, []));

        (await store.ListIndexCheckpointAssetsAsync()).ShouldContain(asset);
        (await store.DiscardIndexCheckpointAsync(asset)).ShouldBe(2);
        (await store.DiscardIndexCheckpointAsync(asset)).ShouldBe(0);
        (await store.LoadIndexCheckpointAsync(asset, Key)).ShouldBeNull();
        (await store.ListIndexCheckpointAssetsAsync()).ShouldNotContain(asset);
    }

    [Fact(DisplayName = "ADR-0064: публикация результата снимает части той же транзакцией; опубликованы ровно переданные лица")]
    public async Task Publication_removes_parts()
    {
        var store = new MediaStore(fixture.Media, new RecordingFileStorage());
        var asset = await SeedAsync(MediaKind.Video, MediaIndexStatus.Processing);
        await store.SaveIndexCheckpointAsync(asset, Key, 0, 1, [Face(0), Face(1)]);

        await store.CompleteIndexingAsync(
            asset, [Face(0) with { CropStoredFileName = "c0.jpg" }, Face(1) with { CropStoredFileName = "c1.jpg" }], "yunet", "sface");

        await using var db = await fixture.Media.CreateDbContextAsync();
        (await db.IndexCheckpoints.CountAsync(c => c.AssetId == asset)).ShouldBe(0);
        (await db.Faces.CountAsync(f => f.AssetId == asset)).ShouldBe(2);
        (await db.Templates.CountAsync(t => t.AssetId == asset)).ShouldBe(2);
        (await db.Assets.AsNoTracking().SingleAsync(x => x.Id == asset)).IndexStatus.ShouldBe(MediaIndexStatus.Indexed);
    }

    [Fact(DisplayName = "ТБ-064, ADR-0064: уничтожение носителя уносит части каскадом; лица незавершённой индексации — в акте")]
    public async Task Purge_removes_parts_and_counts_them_in_act()
    {
        var store = new MediaStore(fixture.Media, new RecordingFileStorage());
        var asset = await SeedAsync(MediaKind.Video, MediaIndexStatus.Failed);
        await store.SaveIndexCheckpointAsync(asset, Key, 0, 1, [Face(0), Face(1)]);

        var result = await new MediaPurger(fixture.Media, new AuditWriter(fixture.Core), new RecordingFileStorage())
            .PurgeAsync(asset, subjectId: 5);

        result.Found.ShouldBeTrue();
        await using (var db = await fixture.Media.CreateDbContextAsync())
        {
            (await db.IndexCheckpoints.AnyAsync(c => c.AssetId == asset)).ShouldBeFalse();
        }

        await using (var db = await fixture.Core.CreateDbContextAsync())
        {
            var act = await db.AuditRecords.SingleAsync(r => r.Action == AuditAction.Purge && r.ObjectRef == $"media:asset:{asset}");
            act.PayloadSensitive.ShouldNotBeNull().ShouldContain("лиц незавершённой индексации 2");
        }
    }

    [Fact(DisplayName = "ТБ-074, ADR-0064: регламент закрытого дела снимает части вместе с шаблонами — и у носителя без опубликованных шаблонов; в акте — их лица; чужой носитель цел, повтор идемпотентен")]
    public async Task Template_purge_removes_parts()
    {
        var store = new MediaStore(fixture.Media, new RecordingFileStorage());
        var pending = await SeedAsync(MediaKind.Video, MediaIndexStatus.Failed);
        var other = await SeedAsync(MediaKind.Video, MediaIndexStatus.Failed);
        await store.SaveIndexCheckpointAsync(pending, Key, 0, 2, [Face(0), Face(2)]);
        await store.SaveIndexCheckpointAsync(other, Key, 0, 0, [Face(0)]);
        var purger = new MediaPurger(fixture.Media, new AuditWriter(fixture.Core), new RecordingFileStorage());

        var result = await purger.PurgeTemplatesAsync([pending], "дело закрыто", subjectId: 5);

        result.AssetsAffected.ShouldBe(1);
        result.TemplatesRemoved.ShouldBe(0); // опубликованных шаблонов не было — в индексе поиска их и не было
        await using (var db = await fixture.Media.CreateDbContextAsync())
        {
            (await db.IndexCheckpoints.AnyAsync(c => c.AssetId == pending)).ShouldBeFalse();
            (await db.IndexCheckpoints.CountAsync(c => c.AssetId == other)).ShouldBe(1);
        }

        await using (var db = await fixture.Core.CreateDbContextAsync())
        {
            var act = await db.AuditRecords.SingleAsync(r => r.Action == AuditAction.Purge && r.ObjectRef == $"media:asset:{pending}");
            act.PayloadSensitive.ShouldNotBeNull().ShouldContain("лиц незавершённой индексации 2");
        }

        (await purger.PurgeTemplatesAsync([pending], "дело закрыто", subjectId: 5)).ShouldBe(TemplatePurgeResult.Empty);
    }

    [Fact(DisplayName = "ТНД-007, ADR-0064: «ждёт обработки» без задачи — фото и видео, кроме носителей импорта, которые задание подаст или ждёт; готовые и аудио — не в списке")]
    public async Task Unqueued_uploads_exclude_import_managed_assets()
    {
        var store = new MediaStore(fixture.Media, new RecordingFileStorage());
        var photo = await SeedAsync(MediaKind.Image, MediaIndexStatus.Uploaded);
        var video = await SeedAsync(MediaKind.Video, MediaIndexStatus.Uploaded);
        var audio = await SeedAsync(MediaKind.Audio, MediaIndexStatus.Uploaded);
        var indexed = await SeedAsync(MediaKind.Video, MediaIndexStatus.Indexed);
        var waiting = await SeedAsync(MediaKind.Video, MediaIndexStatus.Uploaded, MediaImportProcessingState.Waiting);
        var queued = await SeedAsync(MediaKind.Video, MediaIndexStatus.Uploaded, MediaImportProcessingState.Queued);
        var importDone = await SeedAsync(MediaKind.Image, MediaIndexStatus.Uploaded, MediaImportProcessingState.Done);

        var unqueued = await store.ListUnqueuedUploadsAsync();

        unqueued.ShouldContain(photo);
        unqueued.ShouldContain(video);
        unqueued.ShouldContain(importDone); // задание своё отработало — подавать его больше некому
        unqueued.ShouldNotContain(audio);
        unqueued.ShouldNotContain(indexed);
        unqueued.ShouldNotContain(waiting);
        unqueued.ShouldNotContain(queued);
    }

    [Fact(DisplayName = "ТНД-007, ADR-0064: сбой на 8-м кадре — кадры 1–7 сохранены частями, «ошибка» с подсказкой; повтор распознаёт только 3 оставшихся кадра и публикует 10 лиц без дублей; части сняты")]
    public async Task Failed_video_resumes_from_last_saved_frame_without_duplicates()
    {
        var store = new MediaStore(fixture.Media, new RecordingFileStorage());
        var asset = await SeedAsync(MediaKind.Video, MediaIndexStatus.Uploaded);
        var pipeline = new FakePipeline(frameCount: 10);

        // Прогон 1: модель падает на кадре 7 (восьмом по счёту), части — каждые 3 кадра.
        pipeline.FailAtFrame = 7;
        (await pipeline.Indexer(store).IndexAsync(asset)).Success.ShouldBeFalse();

        await using (var db = await fixture.Media.CreateDbContextAsync())
        {
            var row = await db.Assets.AsNoTracking().SingleAsync(a => a.Id == asset);
            row.IndexStatus.ShouldBe(MediaIndexStatus.Failed);
            row.IndexError.ShouldNotBeNull().ShouldContain("0:00:06");
            row.IndexError.ShouldContain("продолжит");
            (await db.Faces.CountAsync(f => f.AssetId == asset)).ShouldBe(0); // в поиск ничего не попало (ТП-005)

            var parts = await db.IndexCheckpoints.AsNoTracking()
                .Where(c => c.AssetId == asset)
                .OrderBy(c => c.FirstFrameIndex)
                .Select(c => new { c.FirstFrameIndex, c.LastFrameIndex, c.FaceCount })
                .ToListAsync();
            parts.Select(p => (p.FirstFrameIndex, p.LastFrameIndex)).ToArray().ShouldBe(new[] { (0, 2), (3, 5), (6, 6) });
            parts.Sum(p => p.FaceCount).ShouldBe(7);
        }

        // Прогон 2 («Переиндексировать»): модели работают только на кадрах 7–9.
        pipeline.FailAtFrame = null;
        pipeline.DetectedFrames.Clear();
        var second = await pipeline.Indexer(store).IndexAsync(asset);

        second.Success.ShouldBeTrue();
        second.Frames.ShouldBe(10);
        second.Faces.ShouldBe(10);
        pipeline.DetectedFrames.ToArray().ShouldBe(RemainingFrames);

        await using (var db = await fixture.Media.CreateDbContextAsync())
        {
            var frames = await db.Faces.AsNoTracking()
                .Where(f => f.AssetId == asset)
                .Select(f => f.Frame!.Index)
                .OrderBy(i => i)
                .ToListAsync();
            frames.ShouldBe(Enumerable.Range(0, 10).ToList()); // каждый кадр — ровно одно лицо, дублей нет
            (await db.Templates.CountAsync(t => t.AssetId == asset)).ShouldBe(10);
            (await db.IndexCheckpoints.AnyAsync(c => c.AssetId == asset)).ShouldBeFalse();

            var row = await db.Assets.AsNoTracking().SingleAsync(a => a.Id == asset);
            row.IndexStatus.ShouldBe(MediaIndexStatus.Indexed);
            row.IndexError.ShouldBeNull();
        }
    }

    /// <summary>Лицо кадра видео: рамка, пригодное качество и шаблон, зависящий от номера кадра.</summary>
    private static IndexedFace Face(int frame) =>
        new(frame, frame * 1000L, new DetectedFace(new BoundingBox(10 + frame, 10, 40, 40), default, 0.9f),
            new FaceQuality(0.9f, true, null), Template(frame));

    /// <summary>Шаблон размерности схемы, различимый по номеру кадра.</summary>
    private static float[] Template(int frame)
    {
        var template = new float[FaceTemplate.Dimensions];
        template[0] = 1f;
        template[1] = frame / 50f;
        return template;
    }

    private async Task<int> SeedAsync(MediaKind kind, MediaIndexStatus status, MediaImportProcessingState? import = null)
    {
        await using var db = await fixture.Media.CreateDbContextAsync();

        int? itemId = null;
        if (import is { } processing)
        {
            // Строка импорта архива (ADR-0041), которой принят носитель: подачу в обработку ведёт задание.
            var item = new MediaImportItem
            {
                Job = new MediaImportJob
                {
                    FolderPath = "архив", ManifestFileName = "опись.csv", ManifestSha256 = new string('0', 64), OperatorUserId = 1,
                },
                RowNumber = 1,
                RelativePath = "a.mp4",
                Kind = MediaImportFileKind.Video,
                ContentType = "video/mp4",
                ByteSize = 3,
                CaseId = 1,
                CaseNumber = "1",
                Classification = 1,
                DivisionId = 7,
                Status = MediaImportItemStatus.Accepted,
                Processing = processing,
            };
            db.ImportItems.Add(item);
            await db.SaveChangesAsync();
            itemId = item.Id;
        }

        var stored = Guid.NewGuid().ToString("N") + ".mp4";
        var asset = new MediaAsset
        {
            Kind = kind,
            OriginalFileName = stored,
            StoredFileName = stored,
            ContentType = kind == MediaKind.Image ? "image/jpeg" : "video/mp4",
            ContentHash = Guid.NewGuid().ToString("N").ToUpperInvariant(),
            ByteSize = 3,
            Classification = 1,
            DivisionId = 7,
            IndexStatus = status,
            TranscriptStatus = TranscriptStatus.NotApplicable,
            ImportItemId = itemId,
        };
        db.Assets.Add(asset);
        await db.SaveChangesAsync();
        return asset.Id;
    }

    /// <summary>
    /// Конвейер с поддельными моделями поверх настоящего хранилища: кадр i раскадровки — одно лицо с шаблоном по номеру
    /// кадра; детектор может упасть на заданном кадре и запоминает, какие кадры распознавал.
    /// </summary>
    private sealed class FakePipeline
    {
        private readonly IFileStorage _files = Substitute.For<IFileStorage>();
        private readonly IFaceDetector _detector = Substitute.For<IFaceDetector>();
        private readonly IFaceEmbedder _embedder = Substitute.For<IFaceEmbedder>();
        private readonly IFaceQualityAssessor _quality = Substitute.For<IFaceQualityAssessor>();
        private readonly IImageTools _imageTools = Substitute.For<IImageTools>();
        private readonly IFrameExtractor _frames = Substitute.For<IFrameExtractor>();
        private readonly ICaseScope _caseScope = Substitute.For<ICaseScope>();
        private readonly MediaTempFiles _tempFiles = new(
            Path.Combine(Path.GetTempPath(), "iscai-integration-tests", Guid.NewGuid().ToString("N")),
            Path.Combine(Path.GetTempPath(), "iscai-integration-tests", Guid.NewGuid().ToString("N") + "-legacy"));

        public FakePipeline(int frameCount)
        {
            _files.OpenReadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(_ => (Stream)new MemoryStream([1, 2, 3]));
            _files.SaveAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(_ => Guid.NewGuid().ToString("N") + ".jpg");

            _detector.ModelVersion.Returns("yunet-test");
            _detector.DetectAsync(Arg.Any<byte[]>(), Arg.Any<CancellationToken>()).Returns(call =>
            {
                int frame = call.ArgAt<byte[]>(0)[0];
                if (frame == FailAtFrame)
                {
                    throw new InvalidOperationException("модель не ответила на кадре " + frame);
                }

                DetectedFrames.Add(frame);
                return new[] { new DetectedFace(new BoundingBox(10 + frame, 10, 40, 40), default, 0.9f) };
            });
            _embedder.ModelVersion.Returns("sface-test");
            _embedder.EmbedAsync(Arg.Any<byte[]>(), Arg.Any<DetectedFace>(), Arg.Any<CancellationToken>())
                .Returns(call => Template(call.ArgAt<byte[]>(0)[0]));
            _quality.Assess(Arg.Any<DetectedFace>(), Arg.Any<int>(), Arg.Any<int>()).Returns(new FaceQuality(0.9f, true, null));

            _imageTools.ReadSize(Arg.Any<byte[]>()).Returns(new ImageSize(640, 480));
            _imageTools.CropJpeg(Arg.Any<byte[]>(), Arg.Any<BoundingBox>(), Arg.Any<float>(), Arg.Any<int>(), Arg.Any<int>()).Returns([9]);
            _imageTools.ThumbnailJpeg(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>()).Returns([7]);
            _imageTools.ComposeStripJpeg(Arg.Any<IReadOnlyList<byte[]>>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>()).Returns([8]);

            _frames.HasVideoStreamAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
            _frames.ExtractAsync(Arg.Any<string>(), Arg.Any<FrameSamplingOptions>(), Arg.Any<CancellationToken>())
                .Returns(_ => Frames(frameCount));
            _caseScope.IsBiometricIndexingAllowedAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);
        }

        /// <summary>Кадр, на котором детектор бросает исключение; <see langword="null"/> — не бросает.</summary>
        public int? FailAtFrame { get; set; }

        /// <summary>Кадры, которые детектор распознал (по порядку).</summary>
        public List<int> DetectedFrames { get; } = [];

        public MediaIndexer Indexer(IMediaStore store) =>
            new(store, _files, _detector, _embedder, _quality, _imageTools, _frames, Substitute.For<IAuditWriter>(), _caseScope,
                new MediaSearchOptions(CheckpointFrames: 3), _tempFiles, Substitute.For<IPersonSuggester>(),
                NullLogger<MediaIndexer>.Instance);

        private static async IAsyncEnumerable<VideoFrame> Frames(int count)
        {
            for (var i = 0; i < count; i++)
            {
                await Task.Yield();
                yield return new VideoFrame(i, TimeSpan.FromSeconds(i), [(byte)i]);
            }
        }
    }
}
