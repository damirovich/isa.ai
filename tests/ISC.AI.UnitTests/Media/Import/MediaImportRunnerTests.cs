using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.BackgroundTasks;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Application;
using ISC.AI.Modules.Media.Application.Features.Assets;
using ISC.AI.Modules.Media.Application.Features.Import;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Media.Import;

/// <summary>
/// Исполнитель импорта (ADR-0041 п. 5–8): допуск и право оператора перед каждым файлом, дело — доступное и с
/// подтверждёнными грифом и подразделением, приём общим путём, журнал до итога строки, прерывание без потерь.
/// </summary>
public sealed class MediaImportRunnerTests : IDisposable
{
    /// <summary>JPEG без EXIF: сигнатура FF D8 FF.</summary>
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 1, 2, 3, 4];

    private const int OperatorId = 7;
    private static readonly AccessContext Access = new("7", 3, [1]);
    private static readonly DateTimeOffset Captured = new(2023, 5, 14, 13, 45, 0, TimeSpan.FromHours(6));

    private readonly string _root = Path.Combine(Path.GetTempPath(), "iscai-run-test-" + Guid.NewGuid().ToString("N"));
    private readonly IMediaImportStore _imports = Substitute.For<IMediaImportStore>();
    private readonly IMediaAdministration _administration = Substitute.For<IMediaAdministration>();
    private readonly IUserAccessReader _accessReader = Substitute.For<IUserAccessReader>();
    private readonly ICaseScope _caseScope = Substitute.For<ICaseScope>();
    private readonly IMediaStore _store = Substitute.For<IMediaStore>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly MediaImportRunner _runner;
    private readonly MediaImportJobRun _job;

    public MediaImportRunnerTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "архив"));
        File.WriteAllBytes(Path.Combine(_root, "архив", "a.jpg"), Jpeg);
        _job = new MediaImportJobRun(5, Path.Combine(_root, "архив"), OperatorId);

        _imports.StartNextJobAsync(Arg.Any<CancellationToken>()).Returns(_job);
        _imports.StopAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        _accessReader.ReadAsync(OperatorId, Arg.Any<CancellationToken>()).Returns(Access);
        _administration.UserCanImportArchiveAsync(OperatorId, Arg.Any<CancellationToken>()).Returns(true);
        _caseScope.GetCaseAsync(10, Access, Arg.Any<CancellationToken>())
            .Returns(new CaseScopeItem(10, "1/2023", "Дело", Classification: 2, DivisionId: 1));
        _store.ReceiveAsync(Arg.Any<MediaAssetDraft>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(new MediaAssetReceipt(70, Duplicate: false, "HASH"));

        _runner = new MediaImportRunner(
            _imports, _administration, _accessReader, _caseScope, _store, _audit,
            new MediaImportOptions([new MediaImportRoot(_root)], 1), MediaUploadOptions.Default, TimeProvider.System);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact(DisplayName = "Работы нет — шаг ничего не делает")]
    public async Task No_job_means_no_work()
    {
        _imports.StartNextJobAsync(Arg.Any<CancellationToken>()).Returns((MediaImportJobRun?)null);
        (await _runner.PrepareAsync()).ShouldBeNull();
        await _accessReader.DidNotReceiveWithAnyArgs().ReadAsync(default);
    }

    [Fact(DisplayName = "Допуск и право оператора — свежим чтением перед шагом; есть — задание в работе")]
    public async Task Prepare_reads_access_and_permission()
    {
        var context = (await _runner.PrepareAsync()).ShouldNotBeNull();

        context.Access.ShouldBe(Access);
        await _accessReader.Received(1).ReadAsync(OperatorId, Arg.Any<CancellationToken>());
        await _administration.Received(1).UserCanImportArchiveAsync(OperatorId, Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Оператор без допуска или с отключённой учёткой — задание останавливается с причиной, в журнал")]
    public async Task Missing_clearance_stops_job()
    {
        _accessReader.ReadAsync(OperatorId, Arg.Any<CancellationToken>()).Returns((AccessContext?)null);

        (await _runner.PrepareAsync()).ShouldBeNull();

        await _imports.Received(1).StopAsync(5, MediaImportRunner.NoClearanceReason, Arg.Any<CancellationToken>());
        await _audit.Received(1).WriteAsync(Arg.Is<AuditEntry>(e => e.PayloadSensitive!.Contains("остановлено системой")), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Право «Импорт архива» снято — задание останавливается")]
    public async Task Missing_permission_stops_job()
    {
        _administration.UserCanImportArchiveAsync(OperatorId, Arg.Any<CancellationToken>()).Returns(false);

        (await _runner.PrepareAsync()).ShouldBeNull();

        await _imports.Received(1).StopAsync(5, MediaImportRunner.NoPermissionReason, Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Каталог архива исключили из настройки — задание останавливается, допуск даже не читается")]
    public async Task Folder_outside_configured_roots_stops_job()
    {
        _imports.StartNextJobAsync(Arg.Any<CancellationToken>()).Returns(new MediaImportJobRun(5, Path.GetTempPath(), OperatorId));

        (await _runner.PrepareAsync()).ShouldBeNull();

        await _imports.Received(1).StopAsync(5, MediaImportRunner.FolderNotAllowedReason, Arg.Any<CancellationToken>());
        await _accessReader.DidNotReceiveWithAnyArgs().ReadAsync(default);
    }

    [Fact(DisplayName = "Строк не осталось — задание завершается и пишется в журнал")]
    public async Task No_items_completes_job()
    {
        _imports.TakeNextItemAsync(5, Arg.Any<CancellationToken>()).Returns((MediaImportWorkItem?)null);
        _imports.TryCompleteJobAsync(5, Arg.Any<CancellationToken>()).Returns(true);

        (await _runner.RunItemAsync(Context())).ShouldBe(MediaImportStep.JobCompleted);

        await _audit.Received(1).WriteAsync(Arg.Is<AuditEntry>(e => e.PayloadSensitive!.Contains("завершено")), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Файл принят от имени оператора: гриф и подразделение дела, сумма и строка импорта в черновике, журнал до итога")]
    public async Task Accepted_file_goes_through_shared_intake()
    {
        Item(Draft(expectedSha: "AB"));

        (await _runner.RunItemAsync(Context())).ShouldBe(MediaImportStep.Processed);

        await _store.Received(1).ReceiveAsync(
            Arg.Is<MediaAssetDraft>(d => d.Classification == 2 && d.DivisionId == 1 && d.UploadedByUserId == OperatorId
                && d.ImportItemId == 100 && d.ExpectedContentHash == "AB" && d.CapturedAt == Captured && d.OriginalFileName == "a.jpg"),
            Arg.Any<Stream>(), Arg.Any<CancellationToken>());
        await _caseScope.Received(1).LinkAssetAsync(10, 70, "двор", OperatorId, Arg.Any<CancellationToken>());
        Received.InOrder(() =>
        {
            _audit.WriteAsync(Arg.Is<AuditEntry>(e => e.Action == AuditAction.Ingest && e.SubjectId == OperatorId
                && e.DivisionId == 1 && e.Classification == 3 && e.ObjectRef!.Contains("result=принят")), Arg.Any<CancellationToken>());
            _imports.CompleteItemAsync(100, Arg.Is<MediaImportItemOutcome>(o => o.Status == MediaImportItemStatus.Accepted
                && o.AssetId == 70 && o.ContentSha256 == "HASH" && o.Processing == MediaImportProcessingState.Waiting), Arg.Any<CancellationToken>());
        });
    }

    [Fact(DisplayName = "Дело недоступно оператору — отказ строки, файл не читается")]
    public async Task Unavailable_case_rejects_row()
    {
        Item(Draft(caseId: 99));

        await _runner.RunItemAsync(Context());

        await Completed(MediaImportItemStatus.Rejected, MediaImportRunner.CaseUnavailableReason);
        await _store.DidNotReceiveWithAnyArgs().ReceiveAsync(default!, default!);
    }

    [Fact(DisplayName = "Гриф дела изменился после подтверждения — отказ строки (ТБ-024)")]
    public async Task Changed_case_classification_rejects_row()
    {
        Item(Draft(classification: 1));

        await _runner.RunItemAsync(Context());

        await Completed(MediaImportItemStatus.Rejected, MediaImportRunner.CaseChangedReason);
        await _store.DidNotReceiveWithAnyArgs().ReceiveAsync(default!, default!);
    }

    [Fact(DisplayName = "Фото без даты в описи и в метаданных — отказ «нет даты съёмки» (ТФ-МЕД-17)")]
    public async Task Photo_without_capture_time_is_rejected()
    {
        Item(Draft(withDate: false));

        await _runner.RunItemAsync(Context());

        await Completed(MediaImportItemStatus.Rejected, MediaIntake.MissingCaptureTimeMessage);
    }

    [Fact(DisplayName = "Контрольная сумма не совпала — отказ с обеими суммами, файл в хранилище не лёг")]
    public async Task Checksum_mismatch_is_rejected()
    {
        Item(Draft(expectedSha: "AB"));
        _store.ReceiveAsync(Arg.Any<MediaAssetDraft>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Throws(new MediaContentHashMismatchException("AB", "CD"));

        await _runner.RunItemAsync(Context());

        await Completed(MediaImportItemStatus.Rejected, "Контрольная сумма не совпала: в описи AB, у файла CD.");
        await _caseScope.DidNotReceiveWithAnyArgs().LinkAssetAsync(default, default, default, default);
    }

    [Theory(DisplayName = "Дубликат: чужой носитель — «дубликат» без обработки; свой (авария посреди приёма) — «принят» с подачей")]
    [InlineData(false, MediaImportItemStatus.Duplicate, MediaImportProcessingState.None)]
    [InlineData(true, MediaImportItemStatus.Accepted, MediaImportProcessingState.Waiting)]
    public async Task Duplicate_receipt(bool own, MediaImportItemStatus status, MediaImportProcessingState processing)
    {
        Item(Draft());
        _store.ReceiveAsync(Arg.Any<MediaAssetDraft>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(new MediaAssetReceipt(70, Duplicate: true, "HASH"));
        _imports.IsAssetFromItemAsync(70, 100, Arg.Any<CancellationToken>()).Returns(own);

        await _runner.RunItemAsync(Context());

        await _imports.Received(1).CompleteItemAsync(
            100, Arg.Is<MediaImportItemOutcome>(o => o.Status == status && o.Processing == processing && o.AssetId == 70), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Остановка посреди файла — строка возвращается в очередь, итог не пишется")]
    public async Task Cancellation_returns_row()
    {
        Item(Draft());
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        _caseScope.GetCaseAsync(10, Access, Arg.Any<CancellationToken>()).Throws(new OperationCanceledException(cancelled.Token));

        (await _runner.RunItemAsync(Context(), cancelled.Token)).ShouldBe(MediaImportStep.Interrupted);

        await _imports.Received(1).ReturnItemAsync(100, Arg.Any<CancellationToken>());
        await _imports.DidNotReceiveWithAnyArgs().CompleteItemAsync(default, default!);
    }

    [Fact(DisplayName = "Журнал не записался — строка не завершается, возвращается в очередь, исполнитель видит сбой строки")]
    public async Task Audit_failure_returns_row()
    {
        Item(Draft());
        _audit.WriteAsync(Arg.Any<AuditEntry>(), Arg.Any<CancellationToken>()).Throws(new InvalidOperationException("БД журнала недоступна"));

        var failure = await Should.ThrowAsync<MediaImportItemException>(() => _runner.RunItemAsync(Context()));

        failure.Work!.ItemId.ShouldBe(100);
        await _imports.Received(1).ReturnItemAsync(100, Arg.Any<CancellationToken>());
        await _imports.DidNotReceiveWithAnyArgs().CompleteItemAsync(default, default!);
    }

    [Fact(DisplayName = "После повторных сбоев строка отклоняется с причиной и журналом")]
    public async Task Reject_after_failures()
    {
        var work = new MediaImportWorkItem(100, 5, Draft());

        await _runner.RejectAsync(Context(), work, "сбой при приёме");

        await Completed(MediaImportItemStatus.Rejected, "сбой при приёме");
        await _audit.Received(1).WriteAsync(Arg.Is<AuditEntry>(e => e.ObjectRef!.Contains("result=отказ")), Arg.Any<CancellationToken>());
    }

    private MediaImportContext Context() => new(_job, Access);

    private void Item(MediaImportItemDraft draft) =>
        _imports.TakeNextItemAsync(5, Arg.Any<CancellationToken>()).Returns(new MediaImportWorkItem(100, 5, draft));

    private Task Completed(MediaImportItemStatus status, string reason) =>
        _imports.Received(1).CompleteItemAsync(
            100, Arg.Is<MediaImportItemOutcome>(o => o.Status == status && o.Reason == reason), Arg.Any<CancellationToken>());

    private static MediaImportItemDraft Draft(
        int caseId = 10, short classification = 2, string? expectedSha = null, bool withDate = true) =>
        new(2, "a.jpg", MediaKind.Image, "image/jpeg", Jpeg.Length, caseId, "1/2023", classification, 1,
            withDate ? Captured : null, false, "камера", "двор", expectedSha);
}

/// <summary>Подача носителей архива в обработку порциями (ADR-0041 п. 7).</summary>
public sealed class MediaImportFeederTests
{
    private readonly IMediaImportStore _imports = Substitute.For<IMediaImportStore>();
    private readonly IMediaStore _store = Substitute.For<IMediaStore>();
    private readonly IBackgroundTaskStore _tasks = Substitute.For<IBackgroundTaskStore>();
    private readonly CountingQueue _queue = new();

    [Fact(DisplayName = "Место занято носителем в обработке — новый не подаётся (новые материалы не ждут архив)")]
    public async Task Busy_slot_blocks_feeding()
    {
        var running = Guid.NewGuid();
        _imports.ListQueuedProcessingAsync(Arg.Any<CancellationToken>()).Returns([new MediaImportFeedItem(1, 10, MediaKind.Image, running, null)]);
        _tasks.GetAsync(running, Arg.Any<CancellationToken>()).Returns(Info(running, BackgroundTaskStatus.Running));

        (await Feeder(slots: 1).FeedAsync()).ShouldBe(0);

        await _imports.DidNotReceiveWithAnyArgs().ListWaitingProcessingAsync(default);
        _queue.Kinds.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Обработка завершилась — носитель отмечен, на освободившееся место подан следующий: видео — лица и речь")]
    public async Task Finished_item_frees_slot_for_next()
    {
        var done = Guid.NewGuid();
        _imports.ListQueuedProcessingAsync(Arg.Any<CancellationToken>()).Returns([new MediaImportFeedItem(1, 10, MediaKind.Image, done, null)]);
        _tasks.GetAsync(done, Arg.Any<CancellationToken>()).Returns(Info(done, BackgroundTaskStatus.Completed));
        _imports.ListWaitingProcessingAsync(1, Arg.Any<CancellationToken>()).Returns([new MediaImportFeedItem(2, 20, MediaKind.Video, null, null)]);

        (await Feeder(slots: 1).FeedAsync()).ShouldBe(1);

        await _imports.Received(1).MarkProcessingDoneAsync(1, Arg.Any<CancellationToken>());
        await _store.Received(1).TryMarkTranscriptionPendingAsync(20, Arg.Any<CancellationToken>());
        _queue.Kinds.ShouldBe([MediaProcessing.IndexingTaskKind, MediaProcessing.TranscriptionTaskKind]);
        await _imports.Received(1).MarkProcessingQueuedAsync(2, _queue.Ids[0], _queue.Ids[1], Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Фото — только лица, аудио — только речь; мест несколько — подаётся столько, сколько свободно")]
    public async Task Feeds_by_kind_up_to_free_slots()
    {
        _imports.ListQueuedProcessingAsync(Arg.Any<CancellationToken>()).Returns([]);
        _imports.ListWaitingProcessingAsync(3, Arg.Any<CancellationToken>()).Returns(
        [
            new MediaImportFeedItem(1, 10, MediaKind.Image, null, null),
            new MediaImportFeedItem(2, 20, MediaKind.Audio, null, null),
        ]);

        (await Feeder(slots: 3).FeedAsync()).ShouldBe(2);

        _queue.Kinds.ShouldBe([MediaProcessing.IndexingTaskKind, MediaProcessing.TranscriptionTaskKind]);
        await _store.DidNotReceive().TryMarkTranscriptionPendingAsync(10, Arg.Any<CancellationToken>());
        await _imports.Received(1).MarkProcessingQueuedAsync(1, _queue.Ids[0], null, Arg.Any<CancellationToken>());
        await _imports.Received(1).MarkProcessingQueuedAsync(2, null, _queue.Ids[1], Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Задачи нет в журнале или она с ошибкой — считается завершённой (иначе место заняло бы навсегда)")]
    public async Task Missing_or_failed_task_counts_as_finished()
    {
        var feeder = Feeder(slots: 1);
        var failed = Guid.NewGuid();
        _tasks.GetAsync(failed, Arg.Any<CancellationToken>()).Returns(Info(failed, BackgroundTaskStatus.Failed));

        (await feeder.IsFinishedAsync(failed)).ShouldBeTrue();
        (await feeder.IsFinishedAsync(Guid.NewGuid())).ShouldBeTrue();
        (await feeder.IsFinishedAsync(null)).ShouldBeTrue();
    }

    private MediaImportFeeder Feeder(int slots) =>
        new(_imports, _store, _queue, _tasks, new MediaImportOptions([], slots));

    private static BackgroundTaskInfo Info(Guid id, BackgroundTaskStatus status) =>
        new(id, "задача", status, DateTime.UtcNow, null, null, null);

    private sealed class CountingQueue : IBackgroundTaskQueue
    {
        public List<string> Kinds { get; } = [];

        public List<Guid> Ids { get; } = [];

        public ValueTask<Guid> EnqueueAsync(
            string kind, Func<IServiceProvider, CancellationToken, Task> work, CancellationToken cancellationToken = default)
        {
            Kinds.Add(kind);
            var id = Guid.NewGuid();
            Ids.Add(id);
            return ValueTask.FromResult(id);
        }
    }
}
