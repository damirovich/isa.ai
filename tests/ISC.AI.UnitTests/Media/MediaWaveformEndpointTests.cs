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
/// Эндпоинт волны аудиозаписи <c>GET /media/waveforms/{id}</c> (ADR-0047): обработчик вызывается напрямую с
/// подставленными портами. Порядок отказов — как у кадра видео: носителя нет, нет контекста, вне допуска (ТБ-020/021),
/// вне дел субъекта (ТБ-071), не аудио — единый 404, и ни одна ветка отказа не читает кэш, не запускает ffmpeg и не
/// пишет журнал. Кандидат взаимного поиска (ADR-0044) волну видит. Готовая волна — из кэша без ffmpeg; нет — строится
/// из оригинала (на месте либо через временную копию, удаляемую после) и запоминается; сбой построения — 404 без
/// записи в кэш. Аудит (ТБ-030) — одна запись View на носитель в окне.
/// </summary>
public sealed class MediaWaveformEndpointTests
{
    private const int AssetId = 42;
    private const string StoredFileName = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.mp3";
    private const string LocalPath = @"C:\store\media-originals\42\" + StoredFileName;
    private static readonly AudioWaveform Built = new(60, [0, 10, 255, 3], AudioWaveform.CurrentVersion);

    private readonly IMediaFileAccess _fileAccess = Substitute.For<IMediaFileAccess>();
    private readonly ICaseScope _caseScope = Substitute.For<ICaseScope>();
    private readonly ISearchSessionStore _sessions = Substitute.For<ISearchSessionStore>();
    private readonly IFileStorage _storage = Substitute.For<IFileStorage>();
    private readonly ILocalFileLocator _localFiles = Substitute.For<ILocalFileLocator>();
    private readonly IAudioWaveformExtractor _extractor = Substitute.For<IAudioWaveformExtractor>();
    private readonly IAudioWaveformCache _cache = Substitute.For<IAudioWaveformCache>();
    private readonly IAccessContextProvider _accessProvider = Substitute.For<IAccessContextProvider>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly MediaViewAuditThrottle _throttle = new();
    private readonly DefaultHttpContext _http = new();

    public MediaWaveformEndpointTests()
    {
        // Счастливый путь по умолчанию: аудио, гриф 1, подразделение 7; субъект 10 в допуске и в деле; волны в кэше нет;
        // оригинал доступен по локальному пути; ffmpeg строит волну.
        _fileAccess.ResolveFrameSourceAsync(AssetId, Arg.Any<CancellationToken>()).Returns(Source());
        _accessProvider.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(new AccessContext("10", 1, [7]));
        _caseScope.IsAssetAccessibleAsync(AssetId, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>()).Returns(true);
        _localFiles.TryGetLocalPath(StoredFileName, MediaFileCategories.Originals, "42").Returns(LocalPath);
        _cache.GetAsync(AssetId, Arg.Any<CancellationToken>()).Returns((AudioWaveform?)null);
        _extractor.ExtractAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Built);
    }

    [Fact(DisplayName = "Носителя нет — 404, контекст доступа не запрашивается, кэш не читается")]
    public async Task Unknown_asset_is_404()
    {
        _fileAccess.ResolveFrameSourceAsync(AssetId, Arg.Any<CancellationToken>()).Returns((MediaFrameSource?)null);

        (await ServeAsync()).ShouldBeOfType<NotFound>();

        await _accessProvider.DidNotReceiveWithAnyArgs().GetCurrentAsync(default);
        await AssertNothingReadAsync();
    }

    [Fact(DisplayName = "ТБ-021: без контекста доступа — 404, не исключение и не 403")]
    public async Task No_access_context_is_404()
    {
        _accessProvider.GetCurrentAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new AccessContextRequiredException());

        (await ServeAsync()).ShouldBeOfType<NotFound>();

        await AssertNothingReadAsync();
    }

    [Theory(DisplayName = "ТБ-020/021: гриф выше допуска или чужое подразделение — 404, область дел даже не спрашивается")]
    [InlineData(2, 7)]
    [InlineData(1, 8)]
    public async Task Outside_clearance_is_404(short classification, int division)
    {
        _fileAccess.ResolveFrameSourceAsync(AssetId, Arg.Any<CancellationToken>())
            .Returns(Source(classification: classification, division: division));

        (await ServeAsync()).ShouldBeOfType<NotFound>();

        await _caseScope.DidNotReceiveWithAnyArgs().IsAssetAccessibleAsync(default, default!, default);
        await AssertNothingReadAsync();
    }

    [Fact(DisplayName = "ТБ-071: носитель вне дел субъекта и не кандидат его поиска — 404")]
    public async Task Outside_cases_is_404()
    {
        _caseScope.IsAssetAccessibleAsync(AssetId, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>()).Returns(false);

        (await ServeAsync()).ShouldBeOfType<NotFound>();

        await AssertNothingReadAsync();
    }

    [Fact(DisplayName = "ADR-0044: кандидат взаимного поиска (материал чужого дела, только просмотр) — волна отдаётся, как и сама запись")]
    public async Task Search_candidate_gets_waveform()
    {
        _caseScope.IsAssetAccessibleAsync(AssetId, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>()).Returns(false);
        _sessions.ListCaseIdsWithCandidateAssetAsync(AssetId, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>()).Returns([3]);
        _caseScope.GetCaseAsync(3, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>())
            .Returns(new CaseScopeItem(3, "12/24", "Дело", 1, 7));

        (await ServeAsync()).ShouldBeOfType<JsonHttpResult<AudioWaveformResponse>>();
    }

    [Theory(DisplayName = "Волна — только у аудио: фото и видео — 404, ffmpeg не запускается")]
    [InlineData(MediaKind.Image)]
    [InlineData(MediaKind.Video)]
    public async Task Not_audio_is_404(MediaKind kind)
    {
        _fileAccess.ResolveFrameSourceAsync(AssetId, Arg.Any<CancellationToken>()).Returns(Source(kind: kind));

        (await ServeAsync()).ShouldBeOfType<NotFound>();

        await AssertNothingReadAsync();
    }

    [Fact(DisplayName = "Волны нет — строится из оригинала НА МЕСТЕ, запоминается, отдаётся JSON (шаг, длительность, пики base64) с nosniff и no-store")]
    public async Task Builds_from_local_original_and_caches()
    {
        var json = (await ServeAsync()).ShouldBeOfType<JsonHttpResult<AudioWaveformResponse>>().Value.ShouldNotBeNull();

        json.StepMs.ShouldBe(60);
        json.DurationMs.ShouldBe(240);
        Convert.FromBase64String(json.Peaks).ShouldBe(Built.Peaks);
        _http.Response.Headers["X-Content-Type-Options"].ToString().ShouldBe("nosniff");
        _http.Response.Headers.CacheControl.ToString().ShouldBe("private, no-store");
        await _extractor.Received(1).ExtractAsync(LocalPath, Arg.Any<CancellationToken>());
        await _cache.Received(1).SaveAsync(AssetId, Built, Arg.Any<CancellationToken>());
        await _storage.DidNotReceiveWithAnyArgs().OpenReadAsync(default!, default!, default!, default);
    }

    [Fact(DisplayName = "Волна уже есть — отдаётся из кэша без ffmpeg и без нового сохранения")]
    public async Task Cached_waveform_is_served_without_ffmpeg()
    {
        _cache.GetAsync(AssetId, Arg.Any<CancellationToken>()).Returns(Built);

        (await ServeAsync()).ShouldBeOfType<JsonHttpResult<AudioWaveformResponse>>();

        await _extractor.DidNotReceiveWithAnyArgs().ExtractAsync(default!, default);
        await _cache.DidNotReceiveWithAnyArgs().SaveAsync(default, default!, default);
    }

    [Fact(DisplayName = "ТБ-030: просмотр волны — запись View «media:waveform:{id}» с грифом и подразделением носителя, одна на носитель в окне")]
    public async Task Audits_view_once_per_window()
    {
        (await ServeAsync()).ShouldBeOfType<JsonHttpResult<AudioWaveformResponse>>();
        (await ServeAsync()).ShouldBeOfType<JsonHttpResult<AudioWaveformResponse>>();

        await _audit.Received(1).WriteAsync(
            Arg.Is<AuditEntry>(e =>
                e.Action == AuditAction.View
                && e.Classification == 1
                && e.SubjectId == 10
                && e.ObjectRef == "media:waveform:42"
                && e.DivisionId == 7),
            Arg.Any<CancellationToken>());
    }

    [Theory(DisplayName = "Сбой построения (ffmpeg, файл без звука, ввод-вывод) — 404, в кэш ничего, журнал не пишется")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Build_failure_is_404_without_cache(bool io)
    {
        _extractor.ExtractAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(io ? new IOException("диск") : new InvalidOperationException("ffmpeg не построил волну записи"));

        (await ServeAsync()).ShouldBeOfType<NotFound>();

        await _cache.DidNotReceiveWithAnyArgs().SaveAsync(default, default!, default);
        await _audit.DidNotReceiveWithAnyArgs().WriteAsync(default!, default);
    }

    [Fact(DisplayName = "Оригинала нет на диске (нет локального пути, копия не открылась) — 404, ffmpeg не запускается")]
    public async Task Missing_on_disk_is_404()
    {
        _localFiles.TryGetLocalPath(StoredFileName, MediaFileCategories.Originals, "42").Returns((string?)null);
        _storage.OpenReadAsync(StoredFileName, MediaFileCategories.Originals, "42", Arg.Any<CancellationToken>())
            .ThrowsAsync(new FileNotFoundException("нет файла"));

        (await ServeAsync()).ShouldBeOfType<NotFound>();

        await _extractor.DidNotReceiveWithAnyArgs().ExtractAsync(default!, default);
    }

    [Fact(DisplayName = "ТБ-064: без локального пути — временная копия %TEMP%\\iscai-media\\waveform-*.mp3; ffmpeg получает её, после — удалена")]
    public async Task Without_local_path_temp_copy_is_used_and_removed()
    {
        byte[] original = [5, 5, 5];
        _storage.OpenReadAsync(StoredFileName, MediaFileCategories.Originals, "42", Arg.Any<CancellationToken>())
            .Returns(_ => new MemoryStream(original));
        string? seenPath = null;
        byte[]? seenBytes = null;
        _extractor.ExtractAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            seenPath = call.Arg<string>();
            seenBytes = File.ReadAllBytes(seenPath);
            return Built;
        });

        (await ServeAsync(localFiles: null)).ShouldBeOfType<JsonHttpResult<AudioWaveformResponse>>();

        seenPath.ShouldNotBeNull();
        seenBytes.ShouldBe(original);
        Path.GetDirectoryName(seenPath).ShouldBe(
            Path.TrimEndingDirectorySeparator(Path.Combine(Path.GetTempPath(), MediaFrameEndpoints.TempFolderName)));
        Path.GetFileName(seenPath).ShouldStartWith(MediaWaveformEndpoints.TempFilePrefix);
        Path.GetExtension(seenPath).ShouldBe(".mp3");
        File.Exists(seenPath).ShouldBeFalse();
    }

    private static MediaFrameSource Source(MediaKind kind = MediaKind.Audio, short classification = 1, int division = 7) =>
        new(AssetId, StoredFileName, kind, "audio/mpeg", classification, division, 240, null);

    private Task<IResult> ServeAsync() => ServeAsync(_localFiles);

    private Task<IResult> ServeAsync(ILocalFileLocator? localFiles) =>
        MediaWaveformEndpoints.ServeWaveformAsync(
            _http, AssetId, _fileAccess, _caseScope, _sessions, _storage, localFiles, _extractor, _cache, _accessProvider, _audit,
            _throttle, NullLogger<MediaWaveformEndpointsCategory>.Instance, CancellationToken.None);

    private async Task AssertNothingReadAsync()
    {
        await _cache.DidNotReceiveWithAnyArgs().GetAsync(default, default);
        await _extractor.DidNotReceiveWithAnyArgs().ExtractAsync(default!, default);
        await _storage.DidNotReceiveWithAnyArgs().OpenReadAsync(default!, default!, default!, default);
        await _audit.DidNotReceiveWithAnyArgs().WriteAsync(default!, default);
    }
}
