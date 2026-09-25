using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.BackgroundTasks;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Application.Features.Assets;
using ISC.AI.Modules.Media.Application.Features.Assets.Commands.SnapshotFrame;
using ISC.AI.Modules.Media.Application.Features.Maintenance;
using ISC.AI.Modules.Media.Data;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using ISC.AI.Persistence.Audit;
using ISC.AI.Persistence.Storage;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Testcontainers.PostgreSql;
using Xunit;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Команда снимка кадра (ADR-0028, предлагаемый ТФ-МЕД-12) на настоящей БД: схемы <c>media</c> (носители),
/// <c>investigation</c> (дела, привязки, решётка по делам и ролям, ТБ-071) и <c>core</c> (журнал аудита, ТБ-030);
/// файлы — настоящее локальное хранилище ядра во временной папке, поэтому оригинал читается «ffmpeg» на месте через
/// <c>ILocalFileLocator</c>, без копии. Подменён только ffmpeg (<see cref="IFrameExtractor"/>): отдаёт синтетические
/// байты PNG, разные для разных моментов и одинаковые для одного — так проверяется дедупликация по хешу (ТНД-002).
/// </summary>
/// <remarks>Требуется Docker. Контейнер общий на класс (<see cref="MediaSnapshotFixture"/>): тесты работают со своими делами, подразделениями и пользователями.</remarks>
[Trait("Category", "Gate")]
public sealed class MediaSnapshotTests(MediaSnapshotFixture fixture) : IClassFixture<MediaSnapshotFixture>
{
    /// <summary>Сигнатура PNG: содержимое кадра для теста не важно, важны байты как есть (ТЭ-007).</summary>
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    [Fact(DisplayName = "ADR-0028: снимок → фото в деле источника с его грифом/подразделением и происхождением; оригинал читается на месте; тот же кадр — дубликат без второй строки; аудит на новый носитель; чужой субъект — NotFound")]
    public async Task Snapshot_is_linked_to_source_case_with_source_fields_dedup_and_audit()
    {
        // Роли: 10 — следователь, владелец дела; 11 — следователь того же подразделения без этого дела;
        // 12 — следователь другого подразделения (ТБ-020: носитель вне допуска неотличим от несуществующего).
        await InvestigationTestKit.AssignRolesAsync(fixture.Investigation,
            (10, InvestigationRole.Investigator),
            (11, InvestigationRole.Investigator),
            (12, InvestigationRole.Investigator));
        var cases = InvestigationTestKit.CreateCaseStore(fixture.Investigation, fixture.Core);
        var scope = InvestigationTestKit.CreateCaseScope(fixture.Investigation, fixture.Core);
        var owner = InvestigationTestKit.Access(10, 9, 5);
        var stranger = InvestigationTestKit.Access(11, 9, 5);
        var otherDivision = InvestigationTestKit.Access(12, 9, 6);

        var (created, caseId) = await cases.CreateAsync(InvestigationTestKit.Draft("СН-1", 5, 2, 10), owner);
        created.ShouldBe(CaseWriteResult.Ok);

        var storage = new LocalFileStorage(Path.Combine(fixture.StorageRoot, "files-1"));
        var store = new MediaStore(fixture.Media, storage);
        var catalog = new MediaCatalog(fixture.Media, new AllowAllAccessPolicy());
        var tempFiles = new MediaTempFiles(Path.Combine(fixture.StorageRoot, "temp-1"), Path.Combine(fixture.StorageRoot, "legacy-1"));

        // Источник — как после загрузки в дело и индексации с пробой: гриф/подразделение дела, привязка, 25 к/с, 8 с.
        var captured = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.FromHours(6));
        var video = await store.ReceiveAsync(
            new MediaAssetDraft("clip.mkv", "video/x-matroska", MediaKind.Video, 2, 5, "камера 2", captured, 10),
            new MemoryStream([0x1A, 0x45, 0xDF, 0xA3, 1, 2, 3]));
        video.Duplicate.ShouldBeFalse();
        await scope.LinkAssetAsync(caseId, video.AssetId, "пост № 1", 10);
        await store.CompleteIndexingAsync(
            video.AssetId, [], "yunet-1", "sface-1", probe: new VideoProbe(25, TimeSpan.FromSeconds(8), 1280, 720));

        // «ffmpeg»: запоминает путь, отдаёт PNG-байты, зависящие только от момента.
        var frames = Substitute.For<IFrameExtractor>();
        var extractedPaths = new List<string>();
        frames.ExtractFrameAsync(Arg.Do<string>(extractedPaths.Add), Arg.Any<TimeSpan>(), FrameImageFormat.Png, null, Arg.Any<CancellationToken>())
            .Returns(call => PngFor(call.ArgAt<TimeSpan>(1)));
        var queue = new RecordingQueue();

        var first = await Handler(owner).Handle(new SnapshotFrameCommand(video.AssetId, 2_010), CancellationToken.None);
        first.Status.ShouldBeTrue(first.StatusMessage);
        var snapshot = first.Data.ShouldNotBeNull();
        snapshot.Duplicate.ShouldBeFalse();
        snapshot.FrameIndex.ShouldBe(50); // 2010·25/1000 = 50,25 → кадр № 50, номинальное время 2000 мс
        snapshot.TimestampMs.ShouldBe(2_000);
        snapshot.StoredFileName.ShouldEndWith(".png");

        // Оригинал читался НА МЕСТЕ: путь внутри корня хранилища, файл на месте; временных копий нет; частота была известна — пробы нет.
        var sourcePath = extractedPaths.ShouldHaveSingleItem();
        sourcePath.ShouldStartWith(storage.Root);
        File.Exists(sourcePath).ShouldBeTrue();
        Directory.Exists(tempFiles.Root).ShouldBeFalse();
        await frames.Received(1).ExtractFrameAsync(sourcePath, VideoProbe.SeekTimeFor(50, 25), FrameImageFormat.Png, null, Arg.Any<CancellationToken>());
        await frames.DidNotReceive().ProbeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());

        // Каталог под решёткой владельца: фото PNG с грифом и подразделением ИСТОЧНИКА (ТБ-070) и происхождением.
        var row = (await catalog.GetAsync(snapshot.AssetId, owner)).ShouldNotBeNull();
        row.Kind.ShouldBe(MediaKind.Image);
        row.ContentType.ShouldBe("image/png");
        row.Classification.ShouldBe<short>(2);
        row.DivisionId.ShouldBe(5);
        row.SourceAssetId.ShouldBe(video.AssetId);
        row.SourceTimestampMs.ShouldBe(2_000);
        row.UploadedByUserId.ShouldBe(10);
        row.OriginalFileName.ShouldBe("clip_кадр50_00-00-02-000.png");
        row.Source.ShouldBe("снимок кадра № 50 (00:00:02.000) из носителя № " + Id(video.AssetId));
        row.CapturedAt.ShouldBe(captured.AddSeconds(2));
        row.IndexStatus.ShouldBe(MediaIndexStatus.Uploaded);
        row.StoredFileName.ShouldBe(snapshot.StoredFileName);
        row.FrameRate.ShouldBeNull();

        // Файл снимка в хранилище — те самые байты «ffmpeg», без пересжатия (ТЭ-007).
        (await ReadAsync(storage, row.StoredFileName, snapshot.AssetId)).ShouldBe(PngFor(VideoProbe.SeekTimeFor(50, 25)));

        // Привязан к делу источника (ТО-инф-08) и доступен через него; индексация лиц поставлена как у загруженного фото.
        (await scope.GetCaseIdForAssetAsync(snapshot.AssetId, owner)).ShouldBe(caseId);
        (await scope.IsAssetAccessibleAsync(snapshot.AssetId, owner)).ShouldBeTrue();
        queue.Kinds.ShouldBe([UploadMediaCommand.IndexingTaskKind]);

        // Тот же кадр ещё раз → дубликат по хешу (ТНД-002): та же строка, второй нет, привязка на месте, задачи нет.
        var again = await Handler(owner).Handle(new SnapshotFrameCommand(video.AssetId, 2_010), CancellationToken.None);
        again.Status.ShouldBeTrue(again.StatusMessage);
        again.Data.ShouldNotBeNull().Duplicate.ShouldBeTrue();
        again.Data.AssetId.ShouldBe(snapshot.AssetId);
        again.Data.StoredFileName.ShouldBe(snapshot.StoredFileName);
        again.StatusMessage.ShouldContain(Id(snapshot.AssetId));
        queue.Kinds.Count.ShouldBe(1);
        (await CountSnapshotsAsync(video.AssetId)).ShouldBe(1);
        (await scope.GetCaseIdForAssetAsync(snapshot.AssetId, owner)).ShouldBe(caseId);

        // Другой кадр — другие байты — новый носитель, тоже в деле источника.
        var other = await Handler(owner).Handle(new SnapshotFrameCommand(video.AssetId, 3_000), CancellationToken.None);
        other.Status.ShouldBeTrue(other.StatusMessage);
        var otherSnapshot = other.Data.ShouldNotBeNull();
        otherSnapshot.Duplicate.ShouldBeFalse();
        otherSnapshot.AssetId.ShouldNotBe(snapshot.AssetId);
        (await scope.GetCaseIdForAssetAsync(otherSnapshot.AssetId, owner)).ShouldBe(caseId);
        queue.Kinds.Count.ShouldBe(2);
        (await CountSnapshotsAsync(video.AssetId)).ShouldBe(2);

        // Аудит (ТБ-030): прямая запись на НОВЫЙ носитель — по одной на снимок и на его дубликат — с грифом и
        // подразделением источника и субъектом; чужому подразделению записи не видны (ТБ-032). Запись сквозного
        // поведения (IAuditableRequest) пишет хост — здесь её нет.
        var reader = new AuditReader(fixture.Core);
        var objectRef = "media:asset:" + Id(snapshot.AssetId) + ":snapshot";
        var page = await reader.QueryAsync(new AuditFilter(Action: AuditAction.Ingest, ObjectRef: objectRef), owner);
        page.TotalCount.ShouldBe(2);
        page.Rows.ShouldAllBe(r => r.SubjectId == 10 && r.Classification == 2 && r.DivisionId == 5 && r.ObjectRef == objectRef);
        page.Rows.ShouldAllBe(r => r.PayloadSensitive != null
            && r.PayloadSensitive.Contains("кадр № 50 (00:00:02.000) из носителя № " + Id(video.AssetId), StringComparison.Ordinal)
            && r.PayloadSensitive.Contains("автоповорот", StringComparison.Ordinal));
        (await reader.QueryAsync(new AuditFilter(Action: AuditAction.Ingest, ObjectRef: objectRef), otherDivision)).TotalCount.ShouldBe(0);

        // Чужие субъекты: следователь того же подразделения без дела (ТБ-071) и следователь другого подразделения (ТБ-020)
        // → NotFound одним текстом; «ffmpeg» не запускался, носителей не прибавилось.
        extractedPaths.Clear();
        var byStranger = await Handler(stranger).Handle(new SnapshotFrameCommand(video.AssetId, 2_010), CancellationToken.None);
        byStranger.StatusCode.ShouldBe(ResponseStatusCode.NotFound);
        byStranger.StatusMessage.ShouldBe(SnapshotFrameCommand.NotFoundMessage);
        var byOtherDivision = await Handler(otherDivision).Handle(new SnapshotFrameCommand(video.AssetId, 2_010), CancellationToken.None);
        byOtherDivision.StatusCode.ShouldBe(ResponseStatusCode.NotFound);
        byOtherDivision.StatusMessage.ShouldBe(SnapshotFrameCommand.NotFoundMessage);
        extractedPaths.ShouldBeEmpty();
        (await CountSnapshotsAsync(video.AssetId)).ShouldBe(2);
        queue.Kinds.Count.ShouldBe(2);

        SnapshotFrameCommand.Handler Handler(AccessContext access) =>
            new(AllowUploads(), AccessOf(access), catalog, scope, store, storage, frames, new AuditWriter(fixture.Core), queue, tempFiles,
                NullLogger<SnapshotFrameCommand.Handler>.Instance, storage);
    }

    [Fact(DisplayName = "ADR-0028: видео до пробы (частоты нет, длительность — таймкод последнего кадра выборки) — хвост записи не отклоняется: запасная проба, кадр вырезан, снимок в деле; дальше точной длительности пробы — отказ до ffmpeg")]
    public async Task Legacy_video_without_frame_rate_is_probed_and_tail_is_reachable()
    {
        await InvestigationTestKit.AssignRolesAsync(fixture.Investigation, (20, InvestigationRole.Investigator));
        var cases = InvestigationTestKit.CreateCaseStore(fixture.Investigation, fixture.Core);
        var scope = InvestigationTestKit.CreateCaseScope(fixture.Investigation, fixture.Core);
        var owner = InvestigationTestKit.Access(20, 9, 7);
        var (created, caseId) = await cases.CreateAsync(InvestigationTestKit.Draft("СН-2", 7, 1, 20), owner);
        created.ShouldBe(CaseWriteResult.Ok);

        var storage = new LocalFileStorage(Path.Combine(fixture.StorageRoot, "files-2"));
        var store = new MediaStore(fixture.Media, storage);
        var catalog = new MediaCatalog(fixture.Media, new AllowAllAccessPolicy());
        var tempFiles = new MediaTempFiles(Path.Combine(fixture.StorageRoot, "temp-2"), Path.Combine(fixture.StorageRoot, "legacy-2"));

        // Видео 2,9 с, проиндексированное ДО ADR-0028 с SampleFps = 1: длительность — 2000 мс (последний кадр выборки), частоты нет.
        var video = await store.ReceiveAsync(
            new MediaAssetDraft("old.mp4", "video/mp4", MediaKind.Video, 1, 7, "регистратор", null, 20),
            new MemoryStream([0, 0, 0, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', 9, 9, 9]));
        await scope.LinkAssetAsync(caseId, video.AssetId, null, 20);
        await store.CompleteIndexingAsync(video.AssetId, [], "yunet-1", "sface-1", durationMs: 2_000);
        var videoRow = (await catalog.GetAsync(video.AssetId, owner)).ShouldNotBeNull();
        videoRow.FrameRate.ShouldBeNull();
        videoRow.DurationMs.ShouldBe(2_000);

        var frames = Substitute.For<IFrameExtractor>();
        var probedPaths = new List<string>();
        frames.ProbeAsync(Arg.Do<string>(probedPaths.Add), Arg.Any<CancellationToken>())
            .Returns(new VideoProbe(25, TimeSpan.FromMilliseconds(2_900), 640, 480));
        frames.ExtractFrameAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), FrameImageFormat.Png, null, Arg.Any<CancellationToken>())
            .Returns(call => PngFor(call.ArgAt<TimeSpan>(1)));
        var queue = new RecordingQueue();
        var handler = new SnapshotFrameCommand.Handler(
            AllowUploads(), AccessOf(owner), catalog, scope, store, storage, frames, new AuditWriter(fixture.Core), queue, tempFiles,
            NullLogger<SnapshotFrameCommand.Handler>.Instance, storage);

        // t = 2500 мс — дальше выборочной длительности 2000, но в пределах записи: проба по оригиналу на месте, кадр вырезан.
        var response = await handler.Handle(new SnapshotFrameCommand(video.AssetId, 2_500), CancellationToken.None);
        response.Status.ShouldBeTrue(response.StatusMessage);
        var snapshot = response.Data.ShouldNotBeNull();
        snapshot.FrameIndex.ShouldBe(62); // 2500·25/1000 = 62,5 → 62 (округление к чётному), номинальное время 2480 мс
        snapshot.TimestampMs.ShouldBe(2_480);
        probedPaths.ShouldHaveSingleItem().ShouldStartWith(storage.Root);
        await frames.Received(1).ExtractFrameAsync(probedPaths[0], VideoProbe.SeekTimeFor(62, 25), FrameImageFormat.Png, null, Arg.Any<CancellationToken>());

        var row = (await catalog.GetAsync(snapshot.AssetId, owner)).ShouldNotBeNull();
        row.SourceAssetId.ShouldBe(video.AssetId);
        row.SourceTimestampMs.ShouldBe(2_480);
        row.Classification.ShouldBe<short>(1);
        row.DivisionId.ShouldBe(7);
        (await scope.GetCaseIdForAssetAsync(snapshot.AssetId, owner)).ShouldBe(caseId);
        queue.Kinds.ShouldBe([UploadMediaCommand.IndexingTaskKind]);

        // Дальше ТОЧНОЙ длительности запасной пробы (2900 мс) — отказ до ffmpeg, носителей не прибавилось.
        frames.ClearReceivedCalls();
        var beyond = await handler.Handle(new SnapshotFrameCommand(video.AssetId, 2_950), CancellationToken.None);
        beyond.StatusCode.ShouldBe(ResponseStatusCode.BadRequest);
        beyond.StatusMessage.ShouldBe(SnapshotFrameCommand.BeyondEndMessage);
        await frames.DidNotReceive().ExtractFrameAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<FrameImageFormat>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
        (await CountSnapshotsAsync(video.AssetId)).ShouldBe(1);
    }

    // ---------- обвязка ----------

    /// <summary>PNG-байты «кадра» в момент <paramref name="at"/>: одинаковые для одного момента, разные для разных.</summary>
    private static byte[] PngFor(TimeSpan at) => [.. PngSignature, .. BitConverter.GetBytes(at.Ticks)];

    private static string Id(int id) => id.ToString(CultureInfo.InvariantCulture);

    private static IMediaAdministration AllowUploads()
    {
        var administration = Substitute.For<IMediaAdministration>();
        administration.CanUploadAsync(Arg.Any<CancellationToken>()).Returns(true);
        return administration;
    }

    private static IAccessContextProvider AccessOf(AccessContext access)
    {
        var provider = Substitute.For<IAccessContextProvider>();
        provider.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(access);
        return provider;
    }

    private static async Task<byte[]> ReadAsync(LocalFileStorage storage, string storedFileName, int assetId)
    {
        await using var stream = await storage.OpenReadAsync(storedFileName, MediaFileCategories.Originals, Id(assetId));
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    /// <summary>Сколько носителей ведут происхождение от видео <paramref name="sourceAssetId"/>.</summary>
    private async Task<int> CountSnapshotsAsync(int sourceAssetId)
    {
        await using var db = fixture.Media.CreateDbContext();
        return await db.Assets.AsNoTracking().CountAsync(a => a.SourceAssetId == sourceAssetId);
    }

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

/// <summary>
/// Общий контейнер Postgres для тестов снимка кадра: схемы <c>media</c>, <c>core</c> (журнал аудита) и
/// <c>investigation</c> (дела, привязки, роли) — и временная папка под файловое хранилище, удаляемая вместе с контейнером.
/// </summary>
public sealed class MediaSnapshotFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = TestPostgres.Create();

    /// <summary>Корень файлов теста: хранилище ядра и временные копии — каждый тест в своём подкаталоге.</summary>
    public string StorageRoot { get; } = Path.Combine(Path.GetTempPath(), "iscai-integration-tests", Guid.NewGuid().ToString("N"));

    /// <summary>Фабрика контекста схемы <c>media</c>.</summary>
    internal MediaContextFactory Media { get; private set; } = null!;

    /// <summary>Фабрика контекста схемы <c>core</c> (журнал аудита).</summary>
    internal CoreContextFactory Core { get; private set; } = null!;

    /// <summary>Фабрика контекста схемы <c>investigation</c> (дела, привязки носителей, роли).</summary>
    internal InvestigationContextFactory Investigation { get; private set; } = null!;

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        var connectionString = _postgres.GetConnectionString();
        Media = new MediaContextFactory(connectionString);
        Core = new CoreContextFactory(connectionString);
        Investigation = new InvestigationContextFactory(connectionString);

        await using (var db = Media.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        await using (var db = Core.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        await InvestigationTestKit.MigrateAsync(Investigation);
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        await _postgres.DisposeAsync();
        try
        {
            if (Directory.Exists(StorageRoot))
            {
                Directory.Delete(StorageRoot, recursive: true);
            }
        }
        catch (IOException)
        {
            // Папка теста — не повод валить прогон.
        }
    }
}
