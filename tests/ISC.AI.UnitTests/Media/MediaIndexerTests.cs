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
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Media;

/// <summary>
/// Конвейер индексации носителя (ТФ-МЕД-02, ТП-005, ТО-мат-06/07): раскадровка видео, качество, шаблон только
/// для пригодных, вырезки для всех, порядок удаления старых вырезок (ПОСЛЕ фиксации строк), компенсация при
/// сбое и отмене, аудит без субъекта с грифом носителя (ТО-инф-11).
/// </summary>
public sealed class MediaIndexerTests
{
    private const int AssetId = 9;

    private static readonly DetectedFace FaceA = new(new BoundingBox(0, 0, 10, 10), default, 0.9f);
    private static readonly DetectedFace FaceB = new(new BoundingBox(20, 20, 10, 10), default, 0.5f);
    private static readonly DetectedFace FaceC = new(new BoundingBox(40, 40, 10, 10), default, 0.8f);

    private readonly IMediaStore _store = Substitute.For<IMediaStore>();
    private readonly IFileStorage _files = Substitute.For<IFileStorage>();
    private readonly IFaceDetector _detector = Substitute.For<IFaceDetector>();
    private readonly IFaceEmbedder _embedder = Substitute.For<IFaceEmbedder>();
    private readonly IFaceQualityAssessor _quality = Substitute.For<IFaceQualityAssessor>();
    private readonly IImageTools _imageTools = Substitute.For<IImageTools>();
    private readonly IFrameExtractor _frames = Substitute.For<IFrameExtractor>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly ICaseScope _caseScope = Substitute.For<ICaseScope>();
    private readonly List<string> _calls = [];
    private int _saved;

    public MediaIndexerTests()
    {
        // По умолчанию дело носителя открыто: запрет индексации — отдельный случай (закрытое дело, ТБ-074).
        _caseScope.IsBiometricIndexingAllowedAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);

        _store.GetForIndexingAsync(AssetId, Arg.Any<CancellationToken>())
            .Returns(new MediaAssetIndexingInfo(AssetId, MediaKind.Video, "src.mp4", "video/mp4", Classification: 2, DivisionId: 7, ["old.jpg"]));
        _store.When(s => s.CompleteIndexingAsync(Arg.Any<int>(), Arg.Any<IReadOnlyList<IndexedFace>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<CancellationToken>()))
            .Do(_ => _calls.Add("complete"));

        // Исходник: байт 5 — «кадр с двумя лицами» (для пути изображения).
        _files.OpenReadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => (Stream)new MemoryStream([5]));
        _files.SaveAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                _saved++;
                _calls.Add("save:new" + _saved + ".jpg");
                return "new" + _saved + ".jpg";
            });
        _files.When(f => f.DeleteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(ci => _calls.Add("delete:" + ci.ArgAt<string>(0)));

        _detector.ModelVersion.Returns("yunet-1");
        _embedder.ModelVersion.Returns("sface-1");
        _embedder.EmbedAsync(Arg.Any<byte[]>(), Arg.Any<DetectedFace>(), Arg.Any<CancellationToken>()).Returns(new float[128]);
        _imageTools.ReadSize(Arg.Any<byte[]>()).Returns(new ImageSize(640, 480));
        _imageTools.CropJpeg(Arg.Any<byte[]>(), Arg.Any<BoundingBox>(), Arg.Any<float>(), Arg.Any<int>(), Arg.Any<int>()).Returns([9]);

        // Кадры: 0 — без лиц, 5 — два лица (B непригодно), 10 — одно лицо.
        _frames.ExtractAsync(Arg.Any<string>(), Arg.Any<FrameSamplingOptions>(), Arg.Any<CancellationToken>())
            .Returns(Frames(
                new VideoFrame(0, TimeSpan.Zero, [0]),
                new VideoFrame(5, TimeSpan.FromSeconds(5), [5]),
                new VideoFrame(10, TimeSpan.FromSeconds(10), [10])));
        _detector.DetectAsync(Arg.Any<byte[]>(), Arg.Any<CancellationToken>()).Returns([]);
        _detector.DetectAsync(Arg.Is<byte[]>(b => b[0] == 5), Arg.Any<CancellationToken>()).Returns([FaceA, FaceB]);
        _detector.DetectAsync(Arg.Is<byte[]>(b => b[0] == 10), Arg.Any<CancellationToken>()).Returns([FaceC]);
        _quality.Assess(Arg.Any<DetectedFace>(), Arg.Any<int>(), Arg.Any<int>()).Returns(new FaceQuality(0.9f, true, null));
        _quality.Assess(FaceB, Arg.Any<int>(), Arg.Any<int>()).Returns(new FaceQuality(0.1f, false, "мало пикселей"));
    }

    [Fact(DisplayName = "Видео: 3 кадра, 3 лица (1 непригодно без шаблона), вырезки для всех, длительность = последний таймкод, аудит Ingest без субъекта")]
    public async Task Video_pipeline_records_faces_with_frames_and_audits()
    {
        var result = await Indexer().IndexAsync(AssetId);

        result.Success.ShouldBeTrue();
        result.Frames.ShouldBe(3);
        result.Faces.ShouldBe(3);
        result.Rejected.ShouldBe(1);

        await _store.Received(1).CompleteIndexingAsync(
            AssetId,
            Arg.Is<IReadOnlyList<IndexedFace>>(faces =>
                faces.Count == 3
                && faces.Select(f => f.FrameIndex).SequenceEqual(new int?[] { 5, 5, 10 })
                && faces.Select(f => f.FrameTimestampMs).SequenceEqual(new long?[] { 5000, 5000, 10000 })
                && faces[1].Template == null && !faces[1].Quality.Acceptable
                && faces[0].Template != null && faces[2].Template != null
                && faces.All(f => f.CropStoredFileName != null)),
            "yunet-1", "sface-1", 10000L, Arg.Any<CancellationToken>());

        // ТО-мат-07: для непригодного лица шаблон не строился.
        await _embedder.DidNotReceive().EmbedAsync(Arg.Any<byte[]>(), FaceB, Arg.Any<CancellationToken>());
        await _embedder.Received(2).EmbedAsync(Arg.Any<byte[]>(), Arg.Any<DetectedFace>(), Arg.Any<CancellationToken>());
        await _files.Received(3).SaveAsync(Arg.Any<Stream>(), ".jpg", MediaFileCategories.FaceCrops, "9", Arg.Any<CancellationToken>());

        // ТП-005: старая вырезка снимается ПОСЛЕ фиксации строк; новые — не трогаются.
        await _files.Received(1).DeleteAsync("old.jpg", MediaFileCategories.FaceCrops, "9", Arg.Any<CancellationToken>());
        _calls.IndexOf("complete").ShouldBeGreaterThan(_calls.IndexOf("save:new3.jpg"));
        _calls.IndexOf("delete:old.jpg").ShouldBeGreaterThan(_calls.IndexOf("complete"));
        _calls.ShouldNotContain(c => c.StartsWith("delete:new", StringComparison.Ordinal));

        // ТО-инф-11: одна запись Ingest, гриф/подразделение носителя, без субъекта.
        await _audit.Received(1).WriteAsync(Arg.Any<AuditEntry>(), Arg.Any<CancellationToken>());
        await _audit.Received(1).WriteAsync(
            Arg.Is<AuditEntry>(e => e.Action == AuditAction.Ingest && e.ObjectRef == "media:asset:9:indexed"
                && e.Classification == 2 && e.DivisionId == 7 && e.SubjectId == null),
            Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Сбой детектора: FailIndexingAsync с причиной, новые вырезки сняты, старые остались, аудита нет")]
    public async Task Detector_failure_marks_failed_and_removes_new_crops()
    {
        _detector.DetectAsync(Arg.Is<byte[]>(b => b[0] == 10), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<DetectedFace>>(_ => throw new InvalidOperationException("модель не загружена"));

        var result = await Indexer().IndexAsync(AssetId);

        result.Success.ShouldBeFalse();
        result.Error.ShouldBe("модель не загружена");
        result.Frames.ShouldBe(3);
        await _store.Received(1).FailIndexingAsync(AssetId, "модель не загружена", Arg.Any<CancellationToken>());
        await _store.DidNotReceive().CompleteIndexingAsync(
            Arg.Any<int>(), Arg.Any<IReadOnlyList<IndexedFace>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
        await _files.Received(1).DeleteAsync("new1.jpg", MediaFileCategories.FaceCrops, "9", Arg.Any<CancellationToken>());
        await _files.Received(1).DeleteAsync("new2.jpg", MediaFileCategories.FaceCrops, "9", Arg.Any<CancellationToken>());
        await _files.DidNotReceive().DeleteAsync("old.jpg", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _audit.DidNotReceive().WriteAsync(Arg.Any<AuditEntry>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Отмена: пробрасывается, носитель переведён в «индексация отменена», строки не записаны")]
    public async Task Cancellation_is_rethrown_and_marked()
    {
        _detector.DetectAsync(Arg.Is<byte[]>(b => b[0] == 10), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<DetectedFace>>(_ => throw new OperationCanceledException());

        await Should.ThrowAsync<OperationCanceledException>(() => Indexer().IndexAsync(AssetId));

        await _store.Received(1).FailIndexingAsync(AssetId, "индексация отменена", Arg.Any<CancellationToken>());
        await _store.DidNotReceive().CompleteIndexingAsync(
            Arg.Any<int>(), Arg.Any<IReadOnlyList<IndexedFace>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
        await _files.DidNotReceive().DeleteAsync("old.jpg", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _files.Received(1).DeleteAsync("new1.jpg", MediaFileCategories.FaceCrops, "9", Arg.Any<CancellationToken>());
        await _audit.DidNotReceive().WriteAsync(Arg.Any<AuditEntry>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Изображение: один кадр без индекса/таймкода, длительность null, раскадровка не вызывается")]
    public async Task Image_is_single_frame_without_timestamps()
    {
        _store.GetForIndexingAsync(AssetId, Arg.Any<CancellationToken>())
            .Returns(new MediaAssetIndexingInfo(AssetId, MediaKind.Image, "img.jpg", "image/jpeg", 2, 7, []));

        var result = await Indexer().IndexAsync(AssetId);

        result.Success.ShouldBeTrue();
        result.Frames.ShouldBe(1);
        result.Faces.ShouldBe(2);
        result.Rejected.ShouldBe(1);
        await _store.Received(1).CompleteIndexingAsync(
            AssetId,
            Arg.Is<IReadOnlyList<IndexedFace>>(faces => faces.Count == 2 && faces.All(f => f.FrameIndex == null && f.FrameTimestampMs == null)),
            "yunet-1", "sface-1", null, Arg.Any<CancellationToken>());
        _frames.DidNotReceive().ExtractAsync(Arg.Any<string>(), Arg.Any<FrameSamplingOptions>(), Arg.Any<CancellationToken>());
        await _files.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Носитель не найден: конвейер не запускается, статус не меняется")]
    public async Task Missing_asset_is_reported_without_side_effects()
    {
        var result = await Indexer().IndexAsync(123);

        result.Success.ShouldBeFalse();
        await _store.DidNotReceive().MarkProcessingAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _store.DidNotReceive().FailIndexingAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "ТБ-074/ADR-0024: у закрытого дела конвейер не строит шаблоны заново — носитель даже не читается на обработку")]
    public async Task Closed_case_asset_is_not_indexed()
    {
        // Регламент по закрытию дела удалил шаблоны; повторная индексация вернула бы биометрию в поиск.
        // Проверяем последний рубеж: конвейер отказывается независимо от того, кто и откуда его позвал.
        _caseScope.IsBiometricIndexingAllowedAsync(AssetId, Arg.Any<CancellationToken>()).Returns(false);

        var result = await Indexer().IndexAsync(AssetId);

        result.Success.ShouldBeFalse();
        result.Error.ShouldNotBeNull().ShouldContain("закрыто");
        result.Faces.ShouldBe(0);

        // Ни статуса «в обработке», ни новых лиц, ни записи «проиндексирован» — ничего не произошло.
        await _store.DidNotReceive().MarkProcessingAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _store.DidNotReceive().CompleteIndexingAsync(
            Arg.Any<int>(), Arg.Any<IReadOnlyList<IndexedFace>>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<long?>(), Arg.Any<CancellationToken>());
        await _audit.DidNotReceive().WriteAsync(Arg.Any<AuditEntry>(), Arg.Any<CancellationToken>());
    }

    private MediaIndexer Indexer() =>
        new(_store, _files, _detector, _embedder, _quality, _imageTools, _frames, _audit, _caseScope,
            new MediaSearchOptions(), NullLogger<MediaIndexer>.Instance);

    private static async IAsyncEnumerable<VideoFrame> Frames(params VideoFrame[] frames)
    {
        foreach (var frame in frames)
        {
            await Task.Yield();
            yield return frame;
        }
    }
}
