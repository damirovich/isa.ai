using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.BackgroundTasks;
using ISC.AI.Abstractions.Security;
using ISC.AI.Abstractions.Storage;
using ISC.AI.Modules.Media.Application.Features.Assets;
using ISC.AI.Modules.Media.Application.Features.Assets.Commands.SnapshotFrame;
using ISC.AI.Modules.Media.Application.Features.Maintenance;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Media;

/// <summary>
/// Снимок кадра как новый носитель дела (ADR-0028, предлагаемый ТФ-МЕД-12): кадр из оригинала на месте, реквизиты
/// происхождения, гриф и подразделение источника (ТБ-070), привязка к делу источника, индексация лиц, аудит (ТБ-030),
/// дедуп (ТНД-002), отказы без раскрытия чужих носителей (ТБ-020/021, ТБ-071), временная копия без локального пути (ТБ-064).
/// </summary>
public sealed class SnapshotFrameCommandTests : IDisposable
{
    private const int SourceId = 10;
    private const int CaseId = 3;
    private const int SnapshotId = 51;

    private static readonly DateTimeOffset CapturedAt = new(2026, 9, 1, 10, 0, 0, TimeSpan.FromHours(6));

    /// <summary>PNG-сигнатура плюс «пиксели»: содержимое кадра для теста не важно, важны байты как есть.</summary>
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];

    /// <summary>Путь оригинала «на месте», который даёт локатор хранилища (ffmpeg замещён — путь условный).</summary>
    private static readonly string LocalPath = Path.Combine("store", MediaFileCategories.Originals, "10", "abc.mp4");

    // Временные копии — в своём каталоге теста: настоящий %TEMP%\iscai-media тесты не трогают.
    private readonly MediaTempFiles _tempFiles = new(
        Path.Combine(Path.GetTempPath(), "iscai-media-unit-tests", Guid.NewGuid().ToString("N")),
        Path.Combine(Path.GetTempPath(), "iscai-media-unit-tests", Guid.NewGuid().ToString("N") + "-legacy"));

    private readonly IMediaAdministration _administration = Substitute.For<IMediaAdministration>();
    private readonly IAccessContextProvider _accessProvider = Substitute.For<IAccessContextProvider>();
    private readonly IMediaCatalog _catalog = Substitute.For<IMediaCatalog>();
    private readonly ICaseScope _caseScope = Substitute.For<ICaseScope>();
    private readonly IMediaStore _store = Substitute.For<IMediaStore>();
    private readonly IFileStorage _files = Substitute.For<IFileStorage>();
    private readonly IFrameExtractor _frames = Substitute.For<IFrameExtractor>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly ILocalFileLocator _locator = Substitute.For<ILocalFileLocator>();
    private readonly RecordingQueue _queue = new();

    public SnapshotFrameCommandTests()
    {
        _administration.CanUploadAsync(Arg.Any<CancellationToken>()).Returns(true);
        _accessProvider.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(new AccessContext("7", 2, [1]));

        // Источник: видео 25 к/с, 10 с, гриф 2, подразделение 1, съёмка известна; снимок читается обратно из каталога.
        _catalog.GetAsync(SourceId, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>()).Returns(VideoRow());
        _catalog.GetAsync(SnapshotId, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>()).Returns(ImageRow(SnapshotId, "snap.png"));
        _caseScope.IsAssetAccessibleAsync(SourceId, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>()).Returns(true);
        _caseScope.GetCaseIdForAssetAsync(SourceId, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>()).Returns(CaseId);

        // Хранилище даёт локальный путь — оригинал читается на месте, без копии.
        _locator.TryGetLocalPath("abc.mp4", MediaFileCategories.Originals, "10").Returns(LocalPath);
        _frames.ExtractFrameAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), FrameImageFormat.Png, null, Arg.Any<CancellationToken>())
            .Returns(Png);
        _store.ReceiveAsync(Arg.Any<MediaAssetDraft>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(new MediaAssetReceipt(SnapshotId, Duplicate: false));
    }

    [Fact(DisplayName = "Успех: PNG кадра № N из оригинала на месте → носитель-фото с грифом/подразделением источника (ТБ-070), реквизитами происхождения, привязкой к делу источника, индексацией лиц и аудитом на новый носитель (ТБ-030)")]
    public async Task Success_creates_snapshot_asset_with_provenance()
    {
        MediaAssetDraft? draft = null;
        _store.ReceiveAsync(Arg.Do<MediaAssetDraft>(d => draft = d), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(new MediaAssetReceipt(SnapshotId, Duplicate: false));

        // 25 к/с, t = 5010 мс → кадр № 125 (5010·25/1000 = 125,25), номинальное время 5000 мс; запрос ffmpeg — на полкадра раньше.
        var response = await HandleAsync(new SnapshotFrameCommand(SourceId, 5010));

        response.Status.ShouldBeTrue();
        var result = response.Data.ShouldNotBeNull();
        result.AssetId.ShouldBe(SnapshotId);
        result.StoredFileName.ShouldBe("snap.png");
        result.Duplicate.ShouldBeFalse();
        result.FrameIndex.ShouldBe(125);
        result.TimestampMs.ShouldBe(5000);

        draft.ShouldNotBeNull();
        draft.Kind.ShouldBe(MediaKind.Image);
        draft.ContentType.ShouldBe("image/png");
        draft.OriginalFileName.ShouldBe("clip_кадр125_00-00-05-000.png");
        draft.Classification.ShouldBe((short)2);
        draft.DivisionId.ShouldBe(1);
        draft.Source.ShouldBe("снимок кадра № 125 (00:00:05.000) из носителя № 10");
        draft.CapturedAt.ShouldBe(CapturedAt.AddSeconds(5));
        draft.UploadedByUserId.ShouldBe(7);
        draft.SourceAssetId.ShouldBe(SourceId);
        draft.SourceTimestampMs.ShouldBe(5000);

        // Кадр вырезан из оригинала НА МЕСТЕ (копии нет), в момент SeekTimeFor(N), PNG в исходном размере; частота известна — пробы нет.
        await _frames.Received(1).ExtractFrameAsync(LocalPath, VideoProbe.SeekTimeFor(125, 25), FrameImageFormat.Png, null, Arg.Any<CancellationToken>());
        await _frames.DidNotReceive().ProbeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _files.DidNotReceive().OpenReadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        // Привязка к делу источника, индексация лиц как у загруженного фото.
        await _caseScope.Received(1).LinkAssetAsync(CaseId, SnapshotId, null, 7, Arg.Any<CancellationToken>());
        _queue.Kinds.ShouldBe([UploadMediaCommand.IndexingTaskKind]);

        // ТБ-030: запись на НОВЫЙ носитель с грифом/подразделением источника и субъектом.
        await _audit.Received(1).WriteAsync(
            Arg.Is<AuditEntry>(e => e.Action == AuditAction.Ingest
                && e.ObjectRef == "media:asset:51:snapshot"
                && e.Classification == 2 && e.DivisionId == 1 && e.SubjectId == 7
                && e.PayloadSensitive != null
                && e.PayloadSensitive.Contains("кадр № 125 (00:00:05.000) из носителя № 10", StringComparison.Ordinal)
                && e.PayloadSensitive.Contains("автоповорот", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Частота кадров неизвестна (видео проиндексировано до ADR-0028) → проба ffprobe сейчас; номер кадра и момент — по её частоте")]
    public async Task Unknown_frame_rate_is_probed()
    {
        _catalog.GetAsync(SourceId, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>()).Returns(VideoRow(frameRate: null));
        _frames.ProbeAsync(LocalPath, Arg.Any<CancellationToken>()).Returns(new VideoProbe(30, TimeSpan.FromSeconds(10), 1920, 1080));

        var response = await HandleAsync(new SnapshotFrameCommand(SourceId, 5010));

        // 30 к/с: 5010·30/1000 = 150,3 → кадр № 150, номинальное время 5000 мс.
        response.Status.ShouldBeTrue();
        response.Data.ShouldNotBeNull().FrameIndex.ShouldBe(150);
        response.Data.TimestampMs.ShouldBe(5000);
        await _frames.Received(1).ProbeAsync(LocalPath, Arg.Any<CancellationToken>());
        await _frames.Received(1).ExtractFrameAsync(LocalPath, VideoProbe.SeekTimeFor(150, 30), FrameImageFormat.Png, null, Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Проба не дала частоты → момент берётся как запрошен, без номера кадра; имя и «Источник» — только с таймкодом")]
    public async Task Null_probe_falls_back_to_requested_timestamp()
    {
        _catalog.GetAsync(SourceId, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>()).Returns(VideoRow(frameRate: null));
        _frames.ProbeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((VideoProbe?)null);
        MediaAssetDraft? draft = null;
        _store.ReceiveAsync(Arg.Do<MediaAssetDraft>(d => draft = d), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(new MediaAssetReceipt(SnapshotId, Duplicate: false));

        var response = await HandleAsync(new SnapshotFrameCommand(SourceId, 5010));

        response.Status.ShouldBeTrue();
        response.Data.ShouldNotBeNull().FrameIndex.ShouldBeNull();
        response.Data.TimestampMs.ShouldBe(5010);
        await _frames.Received(1).ExtractFrameAsync(LocalPath, TimeSpan.FromMilliseconds(5010), FrameImageFormat.Png, null, Arg.Any<CancellationToken>());
        draft.ShouldNotBeNull();
        draft.OriginalFileName.ShouldBe("clip_кадр_00-00-05-010.png");
        draft.Source.ShouldBe("снимок кадра (00:00:05.010) из носителя № 10");
        draft.SourceTimestampMs.ShouldBe(5010);
    }

    [Fact(DisplayName = "Дубликат по хешу (ТНД-002): существующий носитель привязан к делу, индексация не ставится, Duplicate = true, аудит на него есть")]
    public async Task Duplicate_is_linked_without_indexing()
    {
        _store.ReceiveAsync(Arg.Any<MediaAssetDraft>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(new MediaAssetReceipt(50, Duplicate: true));
        _catalog.GetAsync(50, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>()).Returns(ImageRow(50, "dup.png"));

        var response = await HandleAsync(new SnapshotFrameCommand(SourceId, 5000));

        response.Status.ShouldBeTrue();
        var result = response.Data.ShouldNotBeNull();
        result.Duplicate.ShouldBeTrue();
        result.AssetId.ShouldBe(50);
        result.StoredFileName.ShouldBe("dup.png");
        response.StatusMessage.ShouldContain("50");
        await _caseScope.Received(1).LinkAssetAsync(CaseId, 50, null, 7, Arg.Any<CancellationToken>());
        _queue.Kinds.ShouldBeEmpty();
        await _audit.Received(1).WriteAsync(Arg.Is<AuditEntry>(e => e.ObjectRef == "media:asset:50:snapshot"), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Чужой носитель (вне решётки или вне дел субъекта) → NotFound одним текстом: ни ffmpeg, ни приёма, ни привязки, ни дела (ТБ-020/021, ТБ-071)")]
    public async Task Foreign_asset_is_not_found_without_side_effects()
    {
        var outsideGrid = await HandleAsync(new SnapshotFrameCommand(99, 0));
        outsideGrid.StatusCode.ShouldBe(ResponseStatusCode.NotFound);
        outsideGrid.StatusMessage.ShouldBe(SnapshotFrameCommand.NotFoundMessage);

        _caseScope.IsAssetAccessibleAsync(SourceId, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>()).Returns(false);
        var outsideCases = await HandleAsync(new SnapshotFrameCommand(SourceId, 0));
        outsideCases.StatusCode.ShouldBe(ResponseStatusCode.NotFound);
        outsideCases.StatusMessage.ShouldBe(SnapshotFrameCommand.NotFoundMessage);

        await _frames.DidNotReceive().ExtractFrameAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<FrameImageFormat>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
        await _frames.DidNotReceive().ProbeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _caseScope.DidNotReceive().GetCaseIdForAssetAsync(Arg.Any<int>(), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>());
        await _store.DidNotReceive().ReceiveAsync(Arg.Any<MediaAssetDraft>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>());
        await _caseScope.DidNotReceive().LinkAssetAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
        _queue.Kinds.ShouldBeEmpty();
        await _audit.DidNotReceive().WriteAsync(Arg.Any<AuditEntry>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Не видео (фото, аудио) → BadRequest, ffmpeg не вызывается")]
    public async Task Non_video_is_bad_request()
    {
        _catalog.GetAsync(SourceId, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>()).Returns(VideoRow(kind: MediaKind.Image));
        (await HandleAsync(new SnapshotFrameCommand(SourceId, 0))).StatusCode.ShouldBe(ResponseStatusCode.BadRequest);

        _catalog.GetAsync(SourceId, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>()).Returns(VideoRow(kind: MediaKind.Audio));
        var response = await HandleAsync(new SnapshotFrameCommand(SourceId, 0));
        response.StatusCode.ShouldBe(ResponseStatusCode.BadRequest);
        response.StatusMessage.ShouldBe(SnapshotFrameCommand.NotVideoMessage);

        await _frames.DidNotReceive().ExtractFrameAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<FrameImageFormat>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
        await _store.DidNotReceive().ReceiveAsync(Arg.Any<MediaAssetDraft>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Момент дальше ТОЧНОЙ длительности (от пробы, у носителя есть частота) → BadRequest до ffmpeg; без длительности решает ffmpeg")]
    public async Task Timestamp_beyond_probed_duration_is_bad_request()
    {
        var response = await HandleAsync(new SnapshotFrameCommand(SourceId, 10_001));

        response.StatusCode.ShouldBe(ResponseStatusCode.BadRequest);
        response.StatusMessage.ShouldBe(SnapshotFrameCommand.BeyondEndMessage);
        await _frames.DidNotReceive().ExtractFrameAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<FrameImageFormat>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
        await _store.DidNotReceive().ReceiveAsync(Arg.Any<MediaAssetDraft>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>());

        // Длительности нет (контейнер без заголовка) — проверка откладывается до ffmpeg.
        _catalog.GetAsync(SourceId, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>()).Returns(VideoRow(durationMs: null));
        (await HandleAsync(new SnapshotFrameCommand(SourceId, 10_001))).Status.ShouldBeTrue();
    }

    [Fact(DisplayName = "Носитель до ADR-0028 (частоты нет, DurationMs = 2000 — таймкод последнего кадра ВЫБОРКИ): t = 2500 не отклоняется по приблизительной длительности — проба, кадр вырезан → Ok")]
    public async Task Sampled_duration_does_not_reject_tail_of_legacy_video()
    {
        // Видео 2,9 с, проиндексировано с SampleFps = 1: последний кадр выборки — 2000 мс; браузер играет все 2,9 с.
        _catalog.GetAsync(SourceId, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>())
            .Returns(VideoRow(frameRate: null, durationMs: 2_000));
        _frames.ProbeAsync(LocalPath, Arg.Any<CancellationToken>()).Returns(new VideoProbe(25, TimeSpan.FromMilliseconds(2_900), 1280, 720));

        var response = await HandleAsync(new SnapshotFrameCommand(SourceId, 2_500));

        response.Status.ShouldBeTrue();
        // 25 к/с: 2500·25/1000 = 62,5 → кадр № 62 (округление к чётному), номинальное время 2480 мс.
        response.Data.ShouldNotBeNull().FrameIndex.ShouldBe(62);
        response.Data.TimestampMs.ShouldBe(2_480);
        await _frames.Received(1).ProbeAsync(LocalPath, Arg.Any<CancellationToken>());
        await _frames.Received(1).ExtractFrameAsync(LocalPath, VideoProbe.SeekTimeFor(62, 25), FrameImageFormat.Png, null, Arg.Any<CancellationToken>());

        // Та же запись, но момент дальше ТОЧНОЙ длительности запасной пробы → отказ до ffmpeg.
        _frames.ClearReceivedCalls();
        var beyond = await HandleAsync(new SnapshotFrameCommand(SourceId, 2_950));
        beyond.StatusCode.ShouldBe(ResponseStatusCode.BadRequest);
        beyond.StatusMessage.ShouldBe(SnapshotFrameCommand.BeyondEndMessage);
        await _frames.DidNotReceive().ExtractFrameAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<FrameImageFormat>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());

        // Проба без длительности (ffprobe N/A) — решает ffmpeg: кадр есть → Ok.
        _frames.ProbeAsync(LocalPath, Arg.Any<CancellationToken>()).Returns(new VideoProbe(25, null, 1280, 720));
        (await HandleAsync(new SnapshotFrameCommand(SourceId, 2_950))).Status.ShouldBeTrue();
    }

    [Fact(DisplayName = "ffmpeg не дал кадра (момент за концом записи) → BadRequest: ни приёма, ни привязки, ни задачи, ни аудита")]
    public async Task Missing_frame_is_bad_request_without_side_effects()
    {
        _frames.ExtractFrameAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<FrameImageFormat>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns((byte[]?)null);

        var response = await HandleAsync(new SnapshotFrameCommand(SourceId, 9_999));

        response.StatusCode.ShouldBe(ResponseStatusCode.BadRequest);
        response.StatusMessage.ShouldBe(SnapshotFrameCommand.BeyondEndMessage);
        await _store.DidNotReceive().ReceiveAsync(Arg.Any<MediaAssetDraft>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>());
        await _caseScope.DidNotReceive().LinkAssetAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
        _queue.Kinds.ShouldBeEmpty();
        await _audit.DidNotReceive().WriteAsync(Arg.Any<AuditEntry>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Носитель без доступного дела → BadRequest «не привязан к делу», ffmpeg не вызывается")]
    public async Task Unlinked_asset_is_bad_request()
    {
        _caseScope.GetCaseIdForAssetAsync(SourceId, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>()).Returns((int?)null);

        var response = await HandleAsync(new SnapshotFrameCommand(SourceId, 5000));

        response.StatusCode.ShouldBe(ResponseStatusCode.BadRequest);
        response.StatusMessage.ShouldBe(SnapshotFrameCommand.NotLinkedMessage);
        await _frames.DidNotReceive().ExtractFrameAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<FrameImageFormat>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
        await _store.DidNotReceive().ReceiveAsync(Arg.Any<MediaAssetDraft>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Без права загрузки → BadRequest до обращения к носителю")]
    public async Task Without_upload_right_is_bad_request()
    {
        _administration.CanUploadAsync(Arg.Any<CancellationToken>()).Returns(false);

        var response = await HandleAsync(new SnapshotFrameCommand(SourceId, 5000));

        response.StatusCode.ShouldBe(ResponseStatusCode.BadRequest);
        await _catalog.DidNotReceive().GetAsync(Arg.Any<int>(), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>());
    }

    [Theory(DisplayName = "Без локального пути (порт вернул null или не зарегистрирован) → оригинал копируется в управляемый каталог с префиксом snapshot- и удаляется после команды (ТБ-064)")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Without_local_path_temp_copy_is_used_and_removed(bool locatorRegistered)
    {
        _locator.TryGetLocalPath(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns((string?)null);
        _files.OpenReadAsync("abc.mp4", MediaFileCategories.Originals, "10", Arg.Any<CancellationToken>())
            .Returns(_ => (Stream)new MemoryStream([1, 2, 3]));
        string? extractedPath = null;
        byte[]? copied = null;
        _frames.ExtractFrameAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), FrameImageFormat.Png, null, Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                extractedPath = call.ArgAt<string>(0);
                copied = File.ReadAllBytes(extractedPath); // копия существует и полна в момент вызова ffmpeg
                return Png;
            });

        var response = await HandleAsync(new SnapshotFrameCommand(SourceId, 5000), withLocator: locatorRegistered);

        response.Status.ShouldBeTrue();
        extractedPath.ShouldNotBeNull();
        Path.GetDirectoryName(extractedPath).ShouldBe(_tempFiles.Root);
        Path.GetFileName(extractedPath).ShouldStartWith(MediaTempFiles.SnapshotPrefix);
        Path.GetExtension(extractedPath).ShouldBe(".mp4");
        copied.ShouldBe(new byte[] { 1, 2, 3 });
        File.Exists(extractedPath).ShouldBeFalse();
        Directory.EnumerateFiles(_tempFiles.Root).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Сбой ffmpeg после копирования → исключение пробрасывается (его обрабатывает сквозное поведение хоста), временная копия удалена, носитель не принят")]
    public async Task Temp_copy_is_removed_when_extraction_fails()
    {
        _locator.TryGetLocalPath(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>()).Returns((string?)null);
        _files.OpenReadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => (Stream)new MemoryStream([1]));
        _frames.ExtractFrameAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<FrameImageFormat>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns<byte[]?>(_ => throw new InvalidOperationException("ffmpeg не найден"));

        await Should.ThrowAsync<InvalidOperationException>(() => HandleAsync(new SnapshotFrameCommand(SourceId, 5000)));

        Directory.EnumerateFiles(_tempFiles.Root).ShouldBeEmpty();
        await _store.DidNotReceive().ReceiveAsync(Arg.Any<MediaAssetDraft>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>());
    }

    [Theory(DisplayName = "Валидатор: носитель > 0, момент ≥ 0")]
    [InlineData(1, 0L, true)]
    [InlineData(1, 5000L, true)]
    [InlineData(0, 0L, false)]
    [InlineData(1, -1L, false)]
    public void Validator_checks_asset_and_timestamp(int assetId, long timestampMs, bool expected)
    {
        new SnapshotFrameValidator().Validate(new SnapshotFrameCommand(assetId, timestampMs)).IsValid.ShouldBe(expected);
    }

    [Fact(DisplayName = "Реквизиты: таймкод hh:mm:ss.mmm инвариантно (часы не ограничены сутками), имя файла с номером кадра и без, укладывается в предел длины")]
    public void Names_are_invariant_and_bounded()
    {
        SnapshotFrameNames.Timecode(0).ShouldBe("00:00:00.000");
        SnapshotFrameNames.Timecode(3_723_456).ShouldBe("01:02:03.456");
        SnapshotFrameNames.Timecode(100 * 3_600_000L).ShouldBe("100:00:00.000");

        SnapshotFrameNames.FileName("Съёмка с камеры.MKV", 7, 1500).ShouldBe("Съёмка с камеры_кадр7_00-00-01-500.png");
        SnapshotFrameNames.FileName(null, null, 0).ShouldBe("видео_кадр_00-00-00-000.png");
        var longName = SnapshotFrameNames.FileName(new string('и', 300) + ".mp4", 1, 0);
        longName.Length.ShouldBe(MediaFileRules.MaxFileNameLength);
        longName.ShouldEndWith("_кадр1_00-00-00-000.png");

        SnapshotFrameNames.Source(10, 125, 5000).ShouldBe("снимок кадра № 125 (00:00:05.000) из носителя № 10");
        SnapshotFrameNames.Source(10, null, 5010).ShouldBe("снимок кадра (00:00:05.010) из носителя № 10");
        SnapshotFrameNames.AuditPayload(10, 125, 5000).ShouldStartWith("кадр № 125 (00:00:05.000) из носителя № 10; PNG из оригинала");
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

    private async Task<ResponseDto<SnapshotFrameResult>> HandleAsync(SnapshotFrameCommand command, bool withLocator = true)
    {
        var handler = new SnapshotFrameCommand.Handler(
            _administration, _accessProvider, _catalog, _caseScope, _store, _files, _frames, _audit, _queue, _tempFiles,
            NullLogger<SnapshotFrameCommand.Handler>.Instance, withLocator ? _locator : null);
        return await handler.Handle(command, CancellationToken.None);
    }

    /// <summary>Строка носителя-источника: видео (по умолчанию 25 к/с, 10 с), гриф 2, подразделение 1, съёмка известна.</summary>
    private static MediaAssetRow VideoRow(double? frameRate = 25, long? durationMs = 10_000, MediaKind kind = MediaKind.Video) =>
        new(
            SourceId, kind, "clip.mp4", "abc.mp4", "video/mp4", 1_000, durationMs, "камера 2", CapturedAt,
            2, 1, 5, MediaIndexStatus.Indexed, null, null, null, null, DateTime.UtcNow, 0,
            TranscriptStatus.Done, frameRate, 1920, 1080);

    /// <summary>Строка носителя-снимка, как её вернёт каталог после приёма.</summary>
    private static MediaAssetRow ImageRow(int id, string storedFileName) =>
        new(
            id, MediaKind.Image, "snapshot.png", storedFileName, "image/png", 100, null, null, null,
            2, 1, 7, MediaIndexStatus.Uploaded, null, null, null, null, DateTime.UtcNow, 0);

    /// <summary>Очередь-регистратор: запоминает виды поставленных задач, не исполняя их.</summary>
    private sealed class RecordingQueue : IBackgroundTaskQueue
    {
        public List<string> Kinds { get; } = [];

        public ValueTask<Guid> EnqueueAsync(
            string kind, Func<IServiceProvider, CancellationToken, Task> work, CancellationToken cancellationToken = default)
        {
            Kinds.Add(kind);
            return ValueTask.FromResult(Guid.NewGuid());
        }
    }
}
