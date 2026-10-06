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
public sealed class MediaIndexerTests : IDisposable
{
    private const int AssetId = 9;

    // Временные копии — в своём каталоге теста: настоящий %TEMP%\iscai-media тесты не трогают.
    private readonly MediaTempFiles _tempFiles = new(
        Path.Combine(Path.GetTempPath(), "iscai-media-unit-tests", Guid.NewGuid().ToString("N")),
        Path.Combine(Path.GetTempPath(), "iscai-media-unit-tests", Guid.NewGuid().ToString("N") + "-legacy"));

    /// <summary>Один и тот же ненулевой шаблон — «то же лицо» для трекера.</summary>
    private static readonly float[] SameFace = [1f, 0f, 0f];

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
    private readonly IPersonSuggester _suggester = Substitute.For<IPersonSuggester>();
    private readonly List<string> _calls = [];
    private int _saved;

    public MediaIndexerTests()
    {
        // По умолчанию дело носителя открыто: запрет индексации — отдельный случай (закрытое дело, ТБ-074).
        _caseScope.IsBiometricIndexingAllowedAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);

        _store.GetForIndexingAsync(AssetId, Arg.Any<CancellationToken>())
            .Returns(new MediaAssetIndexingInfo(AssetId, MediaKind.Video, "src.mp4", "video/mp4", Classification: 2, DivisionId: 7, ["old.jpg"]));
        _store.When(s => s.CompleteIndexingAsync(Arg.Any<int>(), Arg.Any<IReadOnlyList<IndexedFace>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<VideoProbe?>(), Arg.Any<FilmstripDraft?>(), Arg.Any<CancellationToken>()))
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

        // Видеопоток в файле есть (голосовое «видео» без картинки — отдельный случай).
        _frames.HasVideoStreamAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);

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

    [Fact(DisplayName = "После успешной индексации с лицами запрашиваются предложения связей с фигурантами (ТФ-ПЕР-09)")]
    public async Task Successful_indexing_requests_person_suggestions()
    {
        var result = await Indexer().IndexAsync(AssetId);

        result.Success.ShouldBeTrue();
        await _suggester.Received(1).SuggestAsync(AssetId, SuggestionTrigger.Indexing, Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Сбой предложений связей не отменяет уже записанную индексацию")]
    public async Task Suggestion_failure_does_not_fail_indexing()
    {
        _suggester.SuggestAsync(AssetId, SuggestionTrigger.Indexing, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<PersonSuggestionResult>(new InvalidOperationException("сбой базы")));

        var result = await Indexer().IndexAsync(AssetId);

        result.Success.ShouldBeTrue();
        result.Faces.ShouldBe(3);
        await _store.DidNotReceiveWithAnyArgs().FailIndexingAsync(default, default!, default);
    }

    [Fact(DisplayName = "Видео: одно лицо на соседних кадрах получает общий трек, другое лицо — свой (ТФ-ПЕР-02, ADR-0037)")]
    public async Task Video_faces_on_adjacent_frames_share_track()
    {
        _frames.ExtractAsync(Arg.Any<string>(), Arg.Any<FrameSamplingOptions>(), Arg.Any<CancellationToken>())
            .Returns(Frames(
                new VideoFrame(1, TimeSpan.FromSeconds(1), [5]),
                new VideoFrame(2, TimeSpan.FromSeconds(2), [7])));
        _detector.DetectAsync(Arg.Is<byte[]>(b => b[0] == 7), Arg.Any<CancellationToken>()).Returns([FaceA]);
        _embedder.EmbedAsync(Arg.Any<byte[]>(), Arg.Any<DetectedFace>(), Arg.Any<CancellationToken>()).Returns(SameFace);

        (await Indexer().IndexAsync(AssetId)).Success.ShouldBeTrue();

        // Кадр 1 с: A (с шаблоном) и B (непригодно) — два трека; кадр 2 с: снова A — продолжает свой трек.
        await _store.Received(1).CompleteIndexingAsync(
            AssetId,
            Arg.Is<IReadOnlyList<IndexedFace>>(faces => faces.Select(f => f.TrackId).SequenceEqual(new int?[] { 1, 2, 1 })),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<VideoProbe?>(), Arg.Any<FilmstripDraft?>(), Arg.Any<CancellationToken>());
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
            "yunet-1", "sface-1", 10000L, Arg.Any<VideoProbe?>(), Arg.Any<FilmstripDraft?>(), Arg.Any<CancellationToken>());

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

    [Fact(DisplayName = "Видео: лента кадров из кадров раскадровки — в категорию лент, в ту же фиксацию; прежняя лента снята ПОСЛЕ фиксации (ADR-0038)")]
    public async Task Video_filmstrip_is_built_and_replaces_previous()
    {
        _store.GetForIndexingAsync(AssetId, Arg.Any<CancellationToken>())
            .Returns(new MediaAssetIndexingInfo(AssetId, MediaKind.Video, "src.mp4", "video/mp4", 2, 7, ["old.jpg"], "oldstrip.jpg"));
        _imageTools.ThumbnailJpeg(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>()).Returns([7]);
        _imageTools.ComposeStripJpeg(Arg.Any<IReadOnlyList<byte[]>>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>()).Returns([8]);

        (await Indexer().IndexAsync(AssetId)).Success.ShouldBeTrue();

        // Три кадра раскадровки (0, 5, 10 с) — три плитки 96×72 (кадр 640×480), шаг 5 с; одна картинка ленты.
        _imageTools.Received(3).ThumbnailJpeg(Arg.Any<byte[]>(), 96, MediaIndexer.FilmstripTileHeight, Arg.Any<int>());
        _imageTools.Received(1).ComposeStripJpeg(Arg.Is<IReadOnlyList<byte[]>>(t => t.Count == 3), 96, MediaIndexer.FilmstripTileHeight, Arg.Any<int>());
        await _files.Received(1).SaveAsync(Arg.Any<Stream>(), ".jpg", MediaFileCategories.Filmstrips, "9", Arg.Any<CancellationToken>());
        await _store.Received(1).CompleteIndexingAsync(
            AssetId, Arg.Any<IReadOnlyList<IndexedFace>>(), "yunet-1", "sface-1", 10000L, Arg.Any<VideoProbe?>(),
            new FilmstripDraft("new4.jpg", 3, 5000), Arg.Any<CancellationToken>());

        await _files.Received(1).DeleteAsync("oldstrip.jpg", MediaFileCategories.Filmstrips, "9", Arg.Any<CancellationToken>());
        _calls.IndexOf("delete:oldstrip.jpg").ShouldBeGreaterThan(_calls.IndexOf("complete"));
        _calls.ShouldNotContain("delete:new4.jpg");
    }

    [Fact(DisplayName = "Лента не собралась (сбой уменьшения кадра) — индексация успешна, ленты в фиксации нет, прежняя лента не тронута")]
    public async Task Filmstrip_failure_keeps_indexing_and_previous_strip()
    {
        _store.GetForIndexingAsync(AssetId, Arg.Any<CancellationToken>())
            .Returns(new MediaAssetIndexingInfo(AssetId, MediaKind.Video, "src.mp4", "video/mp4", 2, 7, ["old.jpg"], "oldstrip.jpg"));
        _imageTools.ThumbnailJpeg(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>())
            .Returns<byte[]>(_ => throw new InvalidOperationException("кадр не декодирован"));

        var result = await Indexer().IndexAsync(AssetId);

        result.Success.ShouldBeTrue();
        result.Faces.ShouldBe(3);
        await _store.Received(1).CompleteIndexingAsync(
            AssetId, Arg.Any<IReadOnlyList<IndexedFace>>(), "yunet-1", "sface-1", 10000L, Arg.Any<VideoProbe?>(),
            null, Arg.Any<CancellationToken>());
        // После первого сбоя лента выключена — остальные кадры не уменьшаются; файлов ленты нет.
        _imageTools.Received(1).ThumbnailJpeg(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>());
        await _files.DidNotReceive().SaveAsync(Arg.Any<Stream>(), Arg.Any<string>(), MediaFileCategories.Filmstrips, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _files.DidNotReceive().DeleteAsync("oldstrip.jpg", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Сбой фиксации после записи ленты — новая лента снимается как сирота, прежняя остаётся")]
    public async Task Commit_failure_removes_new_filmstrip()
    {
        _store.GetForIndexingAsync(AssetId, Arg.Any<CancellationToken>())
            .Returns(new MediaAssetIndexingInfo(AssetId, MediaKind.Video, "src.mp4", "video/mp4", 2, 7, ["old.jpg"], "oldstrip.jpg"));
        _imageTools.ThumbnailJpeg(Arg.Any<byte[]>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>()).Returns([7]);
        _imageTools.ComposeStripJpeg(Arg.Any<IReadOnlyList<byte[]>>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>()).Returns([8]);
        _store.CompleteIndexingAsync(
                Arg.Any<int>(), Arg.Any<IReadOnlyList<IndexedFace>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long?>(),
                Arg.Any<VideoProbe?>(), Arg.Any<FilmstripDraft?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("носитель уничтожен")));

        (await Indexer().IndexAsync(AssetId)).Success.ShouldBeFalse();

        await _files.Received(1).DeleteAsync("new4.jpg", MediaFileCategories.Filmstrips, "9", Arg.Any<CancellationToken>());
        await _files.DidNotReceive().DeleteAsync("oldstrip.jpg", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
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
            Arg.Any<int>(), Arg.Any<IReadOnlyList<IndexedFace>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<VideoProbe?>(), Arg.Any<FilmstripDraft?>(), Arg.Any<CancellationToken>());
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
            Arg.Any<int>(), Arg.Any<IReadOnlyList<IndexedFace>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<VideoProbe?>(), Arg.Any<FilmstripDraft?>(), Arg.Any<CancellationToken>());
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
            "yunet-1", "sface-1", null, null, null, Arg.Any<CancellationToken>());
        _frames.DidNotReceive().ExtractAsync(Arg.Any<string>(), Arg.Any<FrameSamplingOptions>(), Arg.Any<CancellationToken>());
        // Проба видеопотока (ADR-0028) — только для видео: у изображения ни частоты кадров, ни длительности нет.
        await _frames.DidNotReceive().ProbeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
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
            Arg.Any<long?>(), Arg.Any<VideoProbe?>(), Arg.Any<FilmstripDraft?>(), Arg.Any<CancellationToken>());
        await _audit.DidNotReceive().WriteAsync(Arg.Any<AuditEntry>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "ADR-0026: аудиозапись — немедленный выход без изменений: ни статуса, ни чтения исходника, ни лиц, ни журнала")]
    public async Task Audio_asset_is_not_indexed_and_nothing_changes()
    {
        _store.GetForIndexingAsync(AssetId, Arg.Any<CancellationToken>())
            .Returns(new MediaAssetIndexingInfo(AssetId, MediaKind.Audio, "voice.ogg", "audio/ogg", 2, 7, []));

        var result = await Indexer().IndexAsync(AssetId);

        result.Success.ShouldBeFalse();
        result.Error.ShouldBe(MediaIndexer.NotApplicableToAudioError);
        result.Faces.ShouldBe(0);

        // Статус носителя уже «неприменимо» (ставится при приёме) и не трогается: ни «в обработке», ни
        // «проиндексировано с нулём лиц», ни «ошибка» — журнал не должен утверждать, что биометрия обрабатывалась.
        await _store.DidNotReceive().MarkProcessingAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _store.DidNotReceive().FailIndexingAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _store.DidNotReceive().CompleteIndexingAsync(
            Arg.Any<int>(), Arg.Any<IReadOnlyList<IndexedFace>>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<long?>(), Arg.Any<VideoProbe?>(), Arg.Any<FilmstripDraft?>(), Arg.Any<CancellationToken>());
        await _files.DidNotReceive().OpenReadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _detector.DidNotReceive().DetectAsync(Arg.Any<byte[]>(), Arg.Any<CancellationToken>());
        await _caseScope.DidNotReceive().IsBiometricIndexingAllowedAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _audit.DidNotReceive().WriteAsync(Arg.Any<AuditEntry>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "ADR-0026: «видео» без видеопотока (голосовое .3gp) → носитель переведён в аудио, лиц «неприменимо»: ни «в обработке», ни ошибки, ни журнала индексации")]
    public async Task Video_without_video_stream_is_reclassified_as_audio()
    {
        _store.GetForIndexingAsync(AssetId, Arg.Any<CancellationToken>())
            .Returns(new MediaAssetIndexingInfo(AssetId, MediaKind.Video, "voice.3gp", "video/3gpp", 2, 7, []));
        string? probedPath = null;
        _frames.HasVideoStreamAsync(Arg.Do<string>(p => probedPath = p), Arg.Any<CancellationToken>()).Returns(false);

        var result = await Indexer().IndexAsync(AssetId);

        result.Success.ShouldBeFalse();
        result.Error.ShouldBe(MediaIndexer.NoVideoStreamError);
        result.Frames.ShouldBe(0);
        result.Faces.ShouldBe(0);

        // Перевод в аудиозаписи — хранилищем (Kind=Audio, лица «неприменимо»).
        await _store.Received(1).ReclassifyAsAudioAsync(AssetId, Arg.Any<CancellationToken>());

        // Биометрия не обрабатывалась — и ни статус, ни журнал этого не утверждают: ни «в обработке», ни
        // «проиндексировано с нулём лиц», ни «ошибка обработки» (сбой ffmpeg на входе без картинки).
        await _store.DidNotReceive().MarkProcessingAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _store.DidNotReceive().FailIndexingAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _store.DidNotReceive().CompleteIndexingAsync(
            Arg.Any<int>(), Arg.Any<IReadOnlyList<IndexedFace>>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<long?>(), Arg.Any<VideoProbe?>(), Arg.Any<FilmstripDraft?>(), Arg.Any<CancellationToken>());
        await _audit.DidNotReceive().WriteAsync(Arg.Any<AuditEntry>(), Arg.Any<CancellationToken>());
        _frames.DidNotReceive().ExtractAsync(Arg.Any<string>(), Arg.Any<FrameSamplingOptions>(), Arg.Any<CancellationToken>());
        await _detector.DidNotReceive().DetectAsync(Arg.Any<byte[]>(), Arg.Any<CancellationToken>());

        // Проба шла по временной копии в управляемом каталоге, и копия удалена.
        probedPath.ShouldNotBeNull();
        Path.GetDirectoryName(probedPath).ShouldBe(_tempFiles.Root);
        Path.GetFileName(probedPath).ShouldStartWith(MediaTempFiles.FramesPrefix);
        Path.GetExtension(probedPath).ShouldBe(".3gp");
        File.Exists(probedPath).ShouldBeFalse();
    }

    [Fact(DisplayName = "Временная копия видео — в управляемом каталоге с префиксом frames-, удаляется после прогона; «плохое» расширение хранилища → .bin")]
    public async Task Temp_copy_lives_in_managed_folder_with_safe_extension()
    {
        // Имя, сохранённое до фильтра при приёме: кавычка в расширении разорвала бы командную строку ffmpeg.
        _store.GetForIndexingAsync(AssetId, Arg.Any<CancellationToken>())
            .Returns(new MediaAssetIndexingInfo(AssetId, MediaKind.Video, "src.mp4\" -y \"out", "video/mp4", 2, 7, []));
        string? extractedPath = null;
        _frames.ExtractAsync(Arg.Do<string>(p => extractedPath = p), Arg.Any<FrameSamplingOptions>(), Arg.Any<CancellationToken>())
            .Returns(Frames(new VideoFrame(0, TimeSpan.Zero, [0])));

        var result = await Indexer().IndexAsync(AssetId);

        result.Success.ShouldBeTrue();
        extractedPath.ShouldNotBeNull();
        Path.GetDirectoryName(extractedPath).ShouldBe(_tempFiles.Root);
        Path.GetFileName(extractedPath).ShouldStartWith(MediaTempFiles.FramesPrefix);
        Path.GetExtension(extractedPath).ShouldBe(MediaFileNames.FallbackExtension);
        extractedPath.ShouldNotContain("\"");
        File.Exists(extractedPath).ShouldBeFalse();
        Directory.EnumerateFiles(_tempFiles.Root).ShouldBeEmpty();
    }

    [Fact(DisplayName = "ADR-0028: проба видеопотока (частота, точная длительность, размер после поворота) читается по временной копии и передаётся в CompleteIndexingAsync вместе с запасной длительностью по раскадровке")]
    public async Task Video_probe_is_read_and_passed_to_store()
    {
        string? probedPath = null;
        _frames.ProbeAsync(Arg.Do<string>(p => probedPath = p), Arg.Any<CancellationToken>())
            .Returns(new VideoProbe(29.97, TimeSpan.FromMilliseconds(12_345), 1080, 1920));

        var result = await Indexer().IndexAsync(AssetId);

        result.Success.ShouldBeTrue();
        result.Faces.ShouldBe(3);
        await _store.Received(1).CompleteIndexingAsync(
            AssetId, Arg.Any<IReadOnlyList<IndexedFace>>(), "yunet-1", "sface-1", 10000L,
            Arg.Is<VideoProbe?>(p => p != null && p.FrameRate == 29.97 && p.DurationMs == 12345 && p.Width == 1080 && p.Height == 1920),
            Arg.Any<FilmstripDraft?>(), Arg.Any<CancellationToken>());

        // Проба — по той же временной копии в управляемом каталоге, что и раскадровка; копия удалена после прогона.
        probedPath.ShouldNotBeNull();
        Path.GetDirectoryName(probedPath).ShouldBe(_tempFiles.Root);
        Path.GetFileName(probedPath).ShouldStartWith(MediaTempFiles.FramesPrefix);
        File.Exists(probedPath).ShouldBeFalse();
    }

    [Fact(DisplayName = "ADR-0028: сбой пробы (ffprobe) индексацию не валит — лица записаны, проба null, длительность по раскадровке, аудит есть, статус не «ошибка»")]
    public async Task Probe_failure_does_not_fail_indexing()
    {
        _frames.ProbeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<VideoProbe?>(_ => throw new InvalidOperationException("ffprobe не найден"));

        var result = await Indexer().IndexAsync(AssetId);

        result.Success.ShouldBeTrue();
        result.Frames.ShouldBe(3);
        result.Faces.ShouldBe(3);
        await _store.Received(1).CompleteIndexingAsync(
            AssetId, Arg.Any<IReadOnlyList<IndexedFace>>(), "yunet-1", "sface-1", 10000L, null, Arg.Any<FilmstripDraft?>(), Arg.Any<CancellationToken>());
        await _store.DidNotReceive().FailIndexingAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _audit.Received(1).WriteAsync(Arg.Any<AuditEntry>(), Arg.Any<CancellationToken>());
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempFiles.Root))
            {
                Directory.Delete(_tempFiles.Root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Каталог теста — не повод валить прогон.
        }
    }

    private MediaIndexer Indexer() =>
        new(_store, _files, _detector, _embedder, _quality, _imageTools, _frames, _audit, _caseScope,
            new MediaSearchOptions(), _tempFiles, _suggester, NullLogger<MediaIndexer>.Instance);

    private static async IAsyncEnumerable<VideoFrame> Frames(params VideoFrame[] frames)
    {
        foreach (var frame in frames)
        {
            await Task.Yield();
            yield return frame;
        }
    }
}
