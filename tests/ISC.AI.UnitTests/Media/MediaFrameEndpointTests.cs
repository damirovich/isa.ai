using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Abstractions.Storage;
using ISC.AI.Modules.Media.Data;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Media;

/// <summary>
/// Эндпоинт кадра видео <c>GET /media/frames/{id}?t=</c> (ADR-0028, ТФ-МЕД-11): обработчик вызывается напрямую с
/// подставленными портами. Проверяется порядок отказов — единый 404 на неверный момент, чужой/несуществующий
/// носитель, отсутствие контекста, выход за допуск (ТБ-020/021) и за дела субъекта (ТБ-071), не-видео, момент за
/// концом записи, «кадра нет» от ffmpeg; ни одна ветка отказа не запускает ffmpeg и не читает оригинал. Момент для
/// ffmpeg — на полкадра раньше кадра № N при известной частоте и как есть без неё; оригинал читается на месте, а без
/// локального пути — через временную копию, удаляемую после; аудит (ТБ-030) — одна запись на носитель в окне.
/// </summary>
public sealed class MediaFrameEndpointTests
{
    private const int AssetId = 42;
    private const string StoredFileName = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.mkv";
    private const string LocalPath = @"C:\store\media-originals\42\" + StoredFileName;
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3];

    private readonly IMediaFileAccess _fileAccess = Substitute.For<IMediaFileAccess>();
    private readonly ICaseScope _caseScope = Substitute.For<ICaseScope>();
    private readonly IFileStorage _storage = Substitute.For<IFileStorage>();
    private readonly ILocalFileLocator _localFiles = Substitute.For<ILocalFileLocator>();
    private readonly IFrameExtractor _frames = Substitute.For<IFrameExtractor>();
    private readonly IAccessContextProvider _accessProvider = Substitute.For<IAccessContextProvider>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly MediaViewAuditThrottle _throttle = new();
    private readonly DefaultHttpContext _http = new();

    public MediaFrameEndpointTests()
    {
        // Счастливый путь по умолчанию: видео 25 к/с длиной 10 с, гриф 1, подразделение 7; субъект 10 в допуске и в деле;
        // оригинал доступен по локальному пути; ffmpeg отдаёт кадр.
        _fileAccess.ResolveFrameSourceAsync(AssetId, Arg.Any<CancellationToken>()).Returns(Source());
        _accessProvider.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(new AccessContext("10", 1, [7]));
        _caseScope.IsAssetAccessibleAsync(AssetId, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>()).Returns(true);
        _localFiles.TryGetLocalPath(StoredFileName, MediaFileCategories.Originals, "42").Returns(LocalPath);
        _frames.ExtractFrameAsync(
                Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<FrameImageFormat>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(Jpeg);
    }

    // ---- отказы: единый 404, ffmpeg не запускается ----

    [Theory(DisplayName = "Момент разбирается строго: не цифры, знак, пробел, дробь, пусто, отсутствует, переполнение — 404 ещё до чтения носителя")]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("+5")]
    [InlineData(" 5")]
    [InlineData("5 ")]
    [InlineData("5.0")]
    [InlineData("1e3")]
    [InlineData("1,000")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("99999999999999999999")]
    public async Task Bad_time_is_404_before_anything_else(string? t)
    {
        var result = await ServeAsync(t);

        result.ShouldBeOfType<NotFound>();
        await _fileAccess.DidNotReceiveWithAnyArgs().ResolveFrameSourceAsync(default, default);
        await AssertNoExtractionAsync();
    }

    [Fact(DisplayName = "Носителя нет — 404, контекст доступа не запрашивается (нечего сравнивать)")]
    public async Task Unknown_asset_is_404()
    {
        _fileAccess.ResolveFrameSourceAsync(AssetId, Arg.Any<CancellationToken>()).Returns((MediaFrameSource?)null);

        (await ServeAsync("1000")).ShouldBeOfType<NotFound>();

        await _accessProvider.DidNotReceiveWithAnyArgs().GetCurrentAsync(default);
        await AssertNoExtractionAsync();
    }

    [Fact(DisplayName = "ТБ-021: без контекста доступа — 404, не исключение и не 403")]
    public async Task No_access_context_is_404()
    {
        _accessProvider.GetCurrentAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new AccessContextRequiredException());

        (await ServeAsync("1000")).ShouldBeOfType<NotFound>();

        await AssertNoExtractionAsync();
    }

    [Theory(DisplayName = "ТБ-020/021: гриф выше допуска или чужое подразделение — 404 (floor ядра), область дел даже не спрашивается")]
    [InlineData(2, 7)]
    [InlineData(1, 8)]
    public async Task Outside_clearance_is_404(short classification, int division)
    {
        _fileAccess.ResolveFrameSourceAsync(AssetId, Arg.Any<CancellationToken>())
            .Returns(Source(classification: classification, division: division));

        (await ServeAsync("1000")).ShouldBeOfType<NotFound>();

        await _caseScope.DidNotReceiveWithAnyArgs().IsAssetAccessibleAsync(default, default!, default);
        await AssertNoExtractionAsync();
    }

    [Fact(DisplayName = "ТБ-071: носитель вне дел субъекта — 404 поверх floor")]
    public async Task Outside_cases_is_404()
    {
        _caseScope.IsAssetAccessibleAsync(AssetId, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>()).Returns(false);

        (await ServeAsync("1000")).ShouldBeOfType<NotFound>();

        await AssertNoExtractionAsync();
    }

    [Theory(DisplayName = "Кадр есть только у видео: фото и аудио — 404")]
    [InlineData(MediaKind.Image)]
    [InlineData(MediaKind.Audio)]
    public async Task Not_video_is_404(MediaKind kind)
    {
        _fileAccess.ResolveFrameSourceAsync(AssetId, Arg.Any<CancellationToken>()).Returns(Source(kind: kind));

        (await ServeAsync("1000")).ShouldBeOfType<NotFound>();

        await AssertNoExtractionAsync();
    }

    [Fact(DisplayName = "Момент за концом ТОЧНОЙ записи (длительность от пробы, частота известна) — 404 без ffmpeg; ровно на конце — кадр отдаётся")]
    public async Task Beyond_duration_is_404_at_end_is_served()
    {
        (await ServeAsync("10001")).ShouldBeOfType<NotFound>();
        await AssertNoExtractionAsync();

        (await ServeAsync("10000")).ShouldBeOfType<FileContentHttpResult>();
    }

    [Fact(DisplayName = "Носитель без пробы: DurationMs — таймкод последнего кадра выборки (округлён вниз), по нему хвост НЕ отсекается — решает ffmpeg")]
    public async Task Sampled_duration_without_fps_does_not_reject_tail()
    {
        // Видео 2,9 с проиндексировано до ADR-0028 при 1 к/с: DurationMs = 2000, частоты нет.
        _fileAccess.ResolveFrameSourceAsync(AssetId, Arg.Any<CancellationToken>())
            .Returns(Source(durationMs: 2000, fps: null));

        (await ServeAsync("2500")).ShouldBeOfType<FileContentHttpResult>();

        await _frames.Received(1).ExtractFrameAsync(
            LocalPath, TimeSpan.FromMilliseconds(2500), FrameImageFormat.Jpeg, 1920, Arg.Any<CancellationToken>());

        // А когда ffmpeg действительно не нашёл кадра (в самом деле за концом) — 404.
        _frames.ExtractFrameAsync(
                Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<FrameImageFormat>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns((byte[]?)null);
        (await ServeAsync("3500")).ShouldBeOfType<NotFound>();
    }

    [Fact(DisplayName = "Длительность ещё не известна (носитель до пробы) — проверка «за концом» пропускается, решает ffmpeg")]
    public async Task Unknown_duration_defers_to_extractor()
    {
        _fileAccess.ResolveFrameSourceAsync(AssetId, Arg.Any<CancellationToken>())
            .Returns(Source(durationMs: null, fps: null));

        (await ServeAsync("3600000")).ShouldBeOfType<FileContentHttpResult>();

        await _frames.Received(1).ExtractFrameAsync(
            LocalPath, TimeSpan.FromMilliseconds(3_600_000), FrameImageFormat.Jpeg, MediaFrameEndpoints.MaxSide, Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "ffmpeg не вырезал кадр (за концом записи) — 404, аудит не пишется")]
    public async Task No_frame_from_extractor_is_404_without_audit()
    {
        _frames.ExtractFrameAsync(
                Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<FrameImageFormat>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns((byte[]?)null);

        (await ServeAsync("1000")).ShouldBeOfType<NotFound>();

        await _audit.DidNotReceiveWithAnyArgs().WriteAsync(default!, default);
    }

    [Fact(DisplayName = "Оригинал есть в БД, но не на диске (нет локального пути и копия не открылась) — 404, ffmpeg не запускается")]
    public async Task Missing_on_disk_is_404()
    {
        _localFiles.TryGetLocalPath(StoredFileName, MediaFileCategories.Originals, "42").Returns((string?)null);
        _storage.OpenReadAsync(StoredFileName, MediaFileCategories.Originals, "42", Arg.Any<CancellationToken>())
            .ThrowsAsync(new FileNotFoundException("нет файла"));

        (await ServeAsync("1000")).ShouldBeOfType<NotFound>();

        // Хранилище здесь спрашивали (и оно ответило «нет файла») — но ffmpeg не запускался и журнал не писался.
        await _storage.Received(1).OpenReadAsync(StoredFileName, MediaFileCategories.Originals, "42", Arg.Any<CancellationToken>());
        await _frames.DidNotReceiveWithAnyArgs().ExtractFrameAsync(default!, default, default, default, default);
        await _audit.DidNotReceiveWithAnyArgs().WriteAsync(default!, default);
    }

    // ---- выбор момента для ffmpeg ----

    [Theory(DisplayName = "Известна частота: момент → кадр № N = round(t·fps), ffmpeg просят на полкадра раньше (SeekTimeFor), JPEG до 1920 по большей стороне")]
    [InlineData(25.0, 1000L, 25L)]
    [InlineData(25.0, 0L, 0L)]
    [InlineData(25.0, 41L, 1L)]
    [InlineData(29.97, 1001L, 30L)]
    [InlineData(30.0, 9_999L, 300L)]
    public async Task Known_fps_seeks_half_frame_before_frame_n(double fps, long t, long expectedFrame)
    {
        _fileAccess.ResolveFrameSourceAsync(AssetId, Arg.Any<CancellationToken>()).Returns(Source(fps: fps));

        (await ServeAsync(t.ToString(System.Globalization.CultureInfo.InvariantCulture))).ShouldBeOfType<FileContentHttpResult>();

        VideoProbe.FrameIndexAt(t, fps).ShouldBe(expectedFrame);
        await _frames.Received(1).ExtractFrameAsync(
            LocalPath, VideoProbe.SeekTimeFor(expectedFrame, fps), FrameImageFormat.Jpeg, 1920, Arg.Any<CancellationToken>());
        await _storage.DidNotReceiveWithAnyArgs().OpenReadAsync(default!, default!, default!, default);
    }

    [Fact(DisplayName = "Частоты нет (носитель до пробы): ffmpeg просят ровно запрошенный момент")]
    public async Task Unknown_fps_seeks_requested_moment()
    {
        _fileAccess.ResolveFrameSourceAsync(AssetId, Arg.Any<CancellationToken>()).Returns(Source(fps: null));

        (await ServeAsync("1234")).ShouldBeOfType<FileContentHttpResult>();

        await _frames.Received(1).ExtractFrameAsync(
            LocalPath, TimeSpan.FromMilliseconds(1234), FrameImageFormat.Jpeg, 1920, Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "SeekTimeFor: с частотой — полкадра до кадра N, без частоты — момент как есть, отрицательный момент — ошибка")]
    public void Seek_time_helper()
    {
        MediaFrameEndpoints.SeekTimeFor(1000, 25).ShouldBe(TimeSpan.FromSeconds(24.5 / 25));
        MediaFrameEndpoints.SeekTimeFor(0, 25).ShouldBe(TimeSpan.Zero);
        MediaFrameEndpoints.SeekTimeFor(1000, null).ShouldBe(TimeSpan.FromSeconds(1));
        MediaFrameEndpoints.SeekTimeFor(1000, 0).ShouldBe(TimeSpan.FromSeconds(1)); // нулевая частота — как «нет»
        Should.Throw<ArgumentOutOfRangeException>(() => MediaFrameEndpoints.SeekTimeFor(-1, 25));
    }

    // ---- успех: ответ, заголовки, аудит, путь к оригиналу ----

    [Fact(DisplayName = "ТБ-030: успешный кадр — JPEG с nosniff и no-store; аудит View «media:frames:{id}» с грифом и подразделением носителя — один раз на носитель в окне, независимо от t")]
    public async Task Success_returns_jpeg_with_headers_and_audits_once_per_asset()
    {
        var first = await ServeAsync("1000");

        var file = first.ShouldBeOfType<FileContentHttpResult>();
        file.ContentType.ShouldBe("image/jpeg");
        file.FileContents.ToArray().ShouldBe(Jpeg);
        file.FileDownloadName.ShouldBeNull(); // показ в <img>, не вложение
        _http.Response.Headers["X-Content-Type-Options"].ToString().ShouldBe("nosniff");
        _http.Response.Headers.CacheControl.ToString().ShouldBe("private, no-store");

        await _audit.Received(1).WriteAsync(
            Arg.Is<AuditEntry>(e =>
                e.Action == AuditAction.View
                && e.Classification == 1
                && e.SubjectId == 10
                && e.ObjectRef == "media:frames:42"
                && e.DivisionId == 7),
            Arg.Any<CancellationToken>());

        // Покадровое листание: другой момент того же носителя — кадр отдаётся, а журнал не растёт (ключ — носитель).
        (await ServeAsync("1040")).ShouldBeOfType<FileContentHttpResult>();
        (await ServeAsync("1080")).ShouldBeOfType<FileContentHttpResult>();
        await _audit.Received(1).WriteAsync(Arg.Any<AuditEntry>(), Arg.Any<CancellationToken>());
        await _frames.Received(3).ExtractFrameAsync(
            Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<FrameImageFormat>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "ТО-инф-10/ТБ-064: есть локальный путь — оригинал читается на месте, копия не делается")]
    public async Task Local_path_is_used_without_copy()
    {
        (await ServeAsync("1000")).ShouldBeOfType<FileContentHttpResult>();

        _localFiles.Received(1).TryGetLocalPath(StoredFileName, MediaFileCategories.Originals, "42");
        await _storage.DidNotReceiveWithAnyArgs().OpenReadAsync(default!, default!, default!, default);
        await _frames.Received(1).ExtractFrameAsync(
            LocalPath, Arg.Any<TimeSpan>(), FrameImageFormat.Jpeg, 1920, Arg.Any<CancellationToken>());
    }

    [Theory(DisplayName = "Локального пути нет (порт не зарегистрирован или вернул null): временная копия в %TEMP%\\iscai-media\\frame-*.<расш.>, ffmpeg получает её, после — удалена")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Without_local_path_temp_copy_is_used_and_removed(bool locatorRegistered)
    {
        byte[] original = [7, 7, 7, 7];
        _localFiles.TryGetLocalPath(StoredFileName, MediaFileCategories.Originals, "42").Returns((string?)null);
        _storage.OpenReadAsync(StoredFileName, MediaFileCategories.Originals, "42", Arg.Any<CancellationToken>())
            .Returns(_ => new MemoryStream(original));

        string? seenPath = null;
        byte[]? seenBytes = null;
        _frames.ExtractFrameAsync(
                Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<FrameImageFormat>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                seenPath = call.Arg<string>();
                seenBytes = File.ReadAllBytes(seenPath); // копия существует и полна В МОМЕНТ вызова ffmpeg
                return Jpeg;
            });

        (await ServeAsync("1000", locatorRegistered ? _localFiles : null)).ShouldBeOfType<FileContentHttpResult>();

        seenPath.ShouldNotBeNull();
        seenBytes.ShouldBe(original);
        Path.GetDirectoryName(seenPath).ShouldBe(
            Path.TrimEndingDirectorySeparator(Path.Combine(Path.GetTempPath(), MediaFrameEndpoints.TempFolderName)));
        Path.GetFileName(seenPath).ShouldStartWith(MediaFrameEndpoints.TempFilePrefix);
        Path.GetExtension(seenPath).ShouldBe(".mkv");
        File.Exists(seenPath).ShouldBeFalse(); // копия материала дела не переживает запрос
    }

    // ---- обвязка ----

    private static MediaFrameSource Source(
        MediaKind kind = MediaKind.Video,
        short classification = 1,
        int division = 7,
        long? durationMs = 10_000,
        double? fps = 25) =>
        new(AssetId, StoredFileName, kind, "video/x-matroska", classification, division, durationMs, fps);

    private Task<IResult> ServeAsync(string? t) => ServeAsync(t, _localFiles);

    private Task<IResult> ServeAsync(string? t, ILocalFileLocator? localFiles) =>
        MediaFrameEndpoints.ServeFrameAsync(
            _http, AssetId, t, _fileAccess, _caseScope, _storage, localFiles, _frames, _accessProvider, _audit, _throttle,
            NullLogger<MediaFrameEndpointsCategory>.Instance, CancellationToken.None);

    private async Task AssertNoExtractionAsync()
    {
        await _frames.DidNotReceiveWithAnyArgs().ExtractFrameAsync(default!, default, default, default, default);
        await _storage.DidNotReceiveWithAnyArgs().OpenReadAsync(default!, default!, default!, default);
        await _audit.DidNotReceiveWithAnyArgs().WriteAsync(default!, default);
    }
}
