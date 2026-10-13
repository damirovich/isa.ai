using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.BackgroundTasks;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Application;
using ISC.AI.Modules.Media.Application.Features.Import;
using ISC.AI.Modules.Media.Data;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Persistence.Audit;
using ISC.AI.Persistence.Storage;
using ISC.AI.Profile.Investigation.Data;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Пакетный импорт архива (ADR-0041) на настоящей БД: хранилище заданий (<c>media.import_job/import_item</c>) со
/// вставкой строк через COPY, счётчиками, остановкой, восстановлением и решёткой строк; сквозной приём файлов из папки
/// архива в дела от имени оператора (настоящие хранилища носителей и дел, журнал ядра); подача в обработку порциями и
/// восстановление после перезапуска; поиск дел по номерам описи под решёткой и ролью.
/// </summary>
/// <remarks>Требуется Docker. Контейнер общий на класс: тесты работают со своими делами, подразделениями и пользователями.</remarks>
[Trait("Category", "Gate")]
public sealed class MediaArchiveImportTests(MediaSnapshotFixture fixture) : IClassFixture<MediaSnapshotFixture>
{
    /// <summary>JPEG без EXIF: сигнатура FF D8 FF.</summary>
    private static readonly byte[] JpegHeader = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01];

    private static readonly DateTimeOffset Captured = new(2023, 5, 14, 13, 45, 0, TimeSpan.FromHours(6));

    [Fact(DisplayName = "Хранилище заданий: строки COPY по порядку описи, счётчики с итогом строки, остановка и продолжение, завершение, восстановление после аварии")]
    public async Task Job_store_lifecycle()
    {
        var store = new MediaImportStore(fixture.Media, new AllowAllAccessPolicy());
        var jobId = await store.CreateJobAsync(new MediaImportJobDraft(
            @"D:\архив", "опись.csv", new string('a', 64), 77,
            [Item(3, "b.mp4", 300, caseId: 1, classification: 1, division: 1), Item(2, "a.jpg", 100, caseId: 1, classification: 1, division: 1)]));

        await StopOtherJobsAsync(jobId);
        var summary = (await store.GetJobAsync(jobId)).ShouldNotBeNull();
        summary.Status.ShouldBe(MediaImportJobStatus.Queued);
        summary.Total.ShouldBe(2);
        summary.BytesTotal.ShouldBe(400);
        summary.ManifestSha256.ShouldBe(new string('A', 64));

        (await store.StartNextJobAsync()).ShouldNotBeNull().JobId.ShouldBe(jobId);
        (await store.GetJobStatusAsync(jobId)).ShouldBe(MediaImportJobStatus.Running);

        // По порядку описи, а не вставки; строка «принимается» до итога.
        var first = (await store.TakeNextItemAsync(jobId)).ShouldNotBeNull();
        first.Draft.RowNumber.ShouldBe(2);
        first.Draft.CapturedAt.ShouldBe(Captured);
        await store.CompleteItemAsync(first.ItemId, MediaImportItemOutcome.Reject("нет файла"));
        await store.CompleteItemAsync(first.ItemId, MediaImportItemOutcome.Reject("повтор не удваивает счётчики"));

        summary = (await store.GetJobAsync(jobId)).ShouldNotBeNull();
        summary.Rejected.ShouldBe(1);
        summary.BytesDone.ShouldBe(100);

        // Авария посреди второй строки: при старте строка возвращается, задание — в очередь.
        var second = (await store.TakeNextItemAsync(jobId)).ShouldNotBeNull();
        (await store.RecoverInterruptedAsync()).ShouldBeGreaterThanOrEqualTo(1);
        (await store.GetJobStatusAsync(jobId)).ShouldBe(MediaImportJobStatus.Queued);
        (await store.TakeNextItemAsync(jobId)).ShouldNotBeNull().ItemId.ShouldBe(second.ItemId);

        // Остановка — из «идёт» и «ждёт»; продолжение — только из «остановлено».
        (await store.StopAsync(jobId, "проверка")).ShouldBeTrue();
        (await store.StopAsync(jobId, "проверка")).ShouldBeFalse();
        (await store.GetJobAsync(jobId))!.StopReason.ShouldBe("проверка");
        (await store.ResumeAsync(jobId)).ShouldBeTrue();
        (await store.ResumeAsync(jobId)).ShouldBeFalse();

        // Остановленная строка возвращена; задание завершается только без строк «ждёт» и «принимается».
        await store.ReturnItemAsync(second.ItemId);
        await StopOtherJobsAsync(jobId);
        (await store.StartNextJobAsync())!.JobId.ShouldBe(jobId);
        (await store.TryCompleteJobAsync(jobId)).ShouldBeFalse();
        var again = (await store.TakeNextItemAsync(jobId)).ShouldNotBeNull();
        await store.CompleteItemAsync(again.ItemId, MediaImportItemOutcome.Reject("нет файла"));
        (await store.TryCompleteJobAsync(jobId)).ShouldBeTrue();

        summary = (await store.GetJobAsync(jobId)).ShouldNotBeNull();
        summary.Status.ShouldBe(MediaImportJobStatus.Completed);
        summary.FinishedAtUtc.ShouldNotBeNull();
        summary.Processed.ShouldBe(2);
        summary.BytesDone.ShouldBe(400);
    }

    [Fact(DisplayName = "Строки задания — под решёткой смотрящего (ТБ-020): гриф выше допуска и чужое подразделение скрыты и считаются числом")]
    public async Task Items_are_filtered_by_viewer_clearance()
    {
        var store = new MediaImportStore(fixture.Media, new AllowAllAccessPolicy());
        var jobId = await store.CreateJobAsync(new MediaImportJobDraft(
            @"D:\архив", "опись.csv", new string('b', 64), 77,
            [
                Item(2, "open.jpg", 1, caseId: 1, classification: 0, division: 31),
                Item(3, "secret.jpg", 1, caseId: 2, classification: 3, division: 31),
                Item(4, "other.jpg", 1, caseId: 3, classification: 0, division: 32),
            ]));

        var viewer = InvestigationTestKit.Access(50, 1, 31);
        var page = await store.ListItemsAsync(jobId, null, viewer, 0, 50);
        page.Rows.Select(r => r.RelativePath).ShouldBe(["open.jpg"]);
        page.Total.ShouldBe(1);
        page.Hidden.ShouldBe(2);

        var report = new List<MediaImportItemView>();
        await foreach (var row in store.StreamItemsAsync(jobId, null, viewer))
        {
            report.Add(row);
        }

        report.Select(r => r.RelativePath).ShouldBe(["open.jpg"]);
    }

    [Fact(DisplayName = "Сквозной приём: файлы архива ложатся в дело с его грифом, дубликат — без второго носителя, чужая контрольная сумма — отказ без записи; журнал по каждому файлу; подача в обработку порциями и после перезапуска")]
    public async Task Runner_imports_archive_into_case()
    {
        // Оператор 60 — Администратор с допуском к подразделению 41; дело ИМП-1 в подразделении 41 с грифом 2.
        await InvestigationTestKit.AssignRolesAsync(fixture.Investigation, (60, InvestigationRole.Administrator));
        var cases = InvestigationTestKit.CreateCaseStore(fixture.Investigation, fixture.Core);
        var scope = InvestigationTestKit.CreateCaseScope(fixture.Investigation, fixture.Core);
        var operatorAccess = InvestigationTestKit.Access(60, 3, 41);
        var (created, caseId) = await cases.CreateAsync(InvestigationTestKit.Draft("ИМП-1", 41, 2, 60), operatorAccess);
        created.ShouldBe(CaseWriteResult.Ok);

        // Архив: два одинаковых файла в разных папках и третий, у которого в описи неверная сумма.
        var root = Path.Combine(fixture.StorageRoot, "import-root");
        var archive = Path.Combine(root, "архив");
        Directory.CreateDirectory(Path.Combine(archive, "1"));
        Directory.CreateDirectory(Path.Combine(archive, "2"));
        var photo = JpegHeader.Concat(Enumerable.Range(0, 5000).Select(i => (byte)(i % 251))).ToArray();
        await File.WriteAllBytesAsync(Path.Combine(archive, "1", "a.jpg"), photo);
        await File.WriteAllBytesAsync(Path.Combine(archive, "2", "a-копия.jpg"), photo);
        await File.WriteAllBytesAsync(Path.Combine(archive, "2", "b.jpg"), JpegHeader.Concat(new byte[] { 9, 9, 9 }).ToArray());
        var sha = Convert.ToHexString(SHA256.HashData(photo));

        var imports = new MediaImportStore(fixture.Media, new AllowAllAccessPolicy());
        var media = new MediaStore(fixture.Media, new LocalFileStorage(Path.Combine(fixture.StorageRoot, "import-files")));
        var options = new MediaImportOptions([new MediaImportRoot(root)], ProcessingSlots: 1);
        var manifest = ImportManifest.Read(System.Text.Encoding.UTF8.GetBytes(
            "файл;дело;дата съёмки;sha256;место\n"
            + $"1/a.jpg;ИМП-1;14.05.2023 13:45;{sha};двор\n"
            + "2/a-копия.jpg;ИМП-1;14.05.2023 13:45;;\n"
            + $"2/b.jpg;ИМП-1;14.05.2023 13:45;{new string('0', 64)};\n"));
        var plan = await new MediaImportPlanner(scope).PlanAsync(
            archive, "опись.csv", manifest, operatorAccess, PlusSix, DateTimeOffset.UtcNow);
        plan.Problems.ShouldBeEmpty();
        plan.Cases.ShouldHaveSingleItem().Target.Case.Classification.ShouldBe<short>(2);

        var jobId = await imports.CreateJobAsync(new MediaImportJobDraft(archive, "опись.csv", plan.ManifestSha256, 60, plan.Items));
        await StopOtherJobsAsync(jobId);

        var accessReader = Substitute.For<IUserAccessReader>();
        accessReader.ReadAsync(60, Arg.Any<CancellationToken>()).Returns(operatorAccess);
        var administration = new MediaAdministration(new UserRoleStore(fixture.Core, fixture.Investigation), new FixedSubjectProvider(60));
        var runner = new MediaImportRunner(
            imports, administration, accessReader, scope, media, new AuditWriter(fixture.Core),
            options, MediaUploadOptions.Default, TimeProvider.System);

        for (var step = 0; step < 10; step++)
        {
            var context = await runner.PrepareAsync();
            if (context is null || await runner.RunItemAsync(context) == MediaImportStep.JobCompleted)
            {
                break;
            }
        }

        var summary = (await imports.GetJobAsync(jobId)).ShouldNotBeNull();
        summary.Status.ShouldBe(MediaImportJobStatus.Completed);
        (summary.Accepted, summary.Duplicates, summary.Rejected).ShouldBe((1, 1, 1));
        summary.ProcessingWaiting.ShouldBe(1, "принятый носитель ждёт подачи, дубликат и отказ — нет");

        var rows = (await imports.ListItemsAsync(jobId, null, operatorAccess, 0, 50)).Rows;
        var accepted = rows.Single(r => r.RelativePath == "1/a.jpg");
        accepted.Status.ShouldBe(MediaImportItemStatus.Accepted);
        accepted.ContentSha256.ShouldBe(sha);
        var duplicate = rows.Single(r => r.RelativePath == "2/a-копия.jpg");
        duplicate.Status.ShouldBe(MediaImportItemStatus.Duplicate);
        duplicate.AssetId.ShouldBe(accepted.AssetId);
        rows.Single(r => r.RelativePath == "2/b.jpg").Reason.ShouldNotBeNull().ShouldContain("Контрольная сумма не совпала");

        // Носитель: гриф и подразделение ДЕЛА (ТБ-070), оператор, дата из описи, происхождение — строка импорта.
        await using (var db = fixture.Media.CreateDbContext())
        {
            var asset = await db.Assets.SingleAsync(a => a.Id == accepted.AssetId);
            (asset.Classification, asset.DivisionId, asset.UploadedByUserId).ShouldBe(((short)2, 41, (int?)60));
            asset.CapturedAt.ShouldBe(Captured);
            asset.ImportItemId.ShouldBe(accepted.ItemId);
            (await db.Assets.CountAsync(a => a.DivisionId == 41)).ShouldBe(1, "файл с неверной суммой в хранилище не лёг");
        }

        (await scope.GetCaseIdForAssetAsync(accepted.AssetId!.Value, operatorAccess)).ShouldBe(caseId);

        // Журнал: по записи на каждый файл, от имени оператора, с грифом не ниже дела и подразделением дела.
        await using (var core = fixture.Core.CreateDbContext())
        {
            var prefix = "media:import:job=" + jobId + ";";
            var records = await core.AuditRecords.Where(r => r.ObjectRef!.StartsWith(prefix)).ToListAsync();
            records.Count.ShouldBe(3);
            records.ShouldAllBe(r => r.SubjectId == 60 && r.DivisionId == 41 && r.Classification >= 2);
            records.Count(r => r.ObjectRef!.Contains("result=отказ")).ShouldBe(1);
        }

        // Подача в обработку: одно место — один носитель; после «перезапуска» поданный возвращается в ожидание.
        var queue = new RecordingQueue();
        var tasks = Substitute.For<IBackgroundTaskStore>();
        tasks.GetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => new BackgroundTaskInfo(call.Arg<Guid>(), "задача", BackgroundTaskStatus.Running, DateTime.UtcNow, null, null, null));
        var feeder = new MediaImportFeeder(imports, media, queue, tasks, options);
        (await feeder.FeedAsync()).ShouldBe(1);
        queue.Kinds.ShouldBe([Modules.Media.Application.Features.Assets.MediaProcessing.IndexingTaskKind]);
        (await feeder.FeedAsync()).ShouldBe(0, "место занято: задача носителя ещё выполняется");

        (await imports.RecoverProcessingAsync(_ => Task.FromResult(false))).ShouldBe(1);
        (await imports.GetJobAsync(jobId))!.ProcessingWaiting.ShouldBe(1);
    }

    [Fact(DisplayName = "Перезапуск: носитель архива, ещё не поданный в обработку, не переводится в «ошибка» — он ждёт очереди; обычный — переводится")]
    public async Task Startup_recovery_skips_waiting_import_assets()
    {
        var media = new MediaStore(fixture.Media, new LocalFileStorage(Path.Combine(fixture.StorageRoot, "recovery-files")));
        var imports = new MediaImportStore(fixture.Media, new AllowAllAccessPolicy());

        var jobId = await imports.CreateJobAsync(new MediaImportJobDraft(
            @"D:\архив", "опись.csv", new string('c', 64), 77, [Item(2, "запись.mp3", 4, caseId: 1, classification: 0, division: 51, kind: MediaKind.Audio)]));
        await imports.StartNextJobAsync();
        var work = (await imports.TakeNextItemAsync(jobId)).ShouldNotBeNull();
        var archived = await media.ReceiveAsync(
            new MediaAssetDraft("запись.mp3", "audio/mpeg", MediaKind.Audio, 0, 51, ImportItemId: work.ItemId),
            new MemoryStream([0x49, 0x44, 0x33, 1, 2, 3, 4]));
        await imports.CompleteItemAsync(work.ItemId, new MediaImportItemOutcome(
            MediaImportItemStatus.Accepted, null, archived.AssetId, archived.ContentHash, MediaImportProcessingState.Waiting));

        var ordinary = await media.ReceiveAsync(
            new MediaAssetDraft("голос.mp3", "audio/mpeg", MediaKind.Audio, 0, 51),
            new MemoryStream([0x49, 0x44, 0x33, 9, 9, 9, 9]));

        await media.RecoverInterruptedAsync();

        await using var db = fixture.Media.CreateDbContext();
        (await db.Assets.SingleAsync(a => a.Id == archived.AssetId)).TranscriptStatus.ShouldBe(TranscriptStatus.Pending);
        (await db.Assets.SingleAsync(a => a.Id == ordinary.AssetId)).TranscriptStatus.ShouldBe(TranscriptStatus.Failed);
    }

    [Fact(DisplayName = "Дела по номерам описи: только доступные по решётке и роли, номер без пробелов по краям, одинаковый номер в двух подразделениях — оба с кодами, отдел — от вышестоящего")]
    public async Task Cases_are_found_by_number_under_access()
    {
        await InvestigationTestKit.AssignRolesAsync(fixture.Investigation, (70, InvestigationRole.Head), (71, InvestigationRole.Investigator));
        var divisions = new DivisionAdminStore(fixture.Investigation, fixture.Core);
        var root = await divisions.CreateAsync("ОПУ импорт", null, parentId: null, direction: null);
        var otm = await divisions.CreateAsync("Отдел ОТМ импорт", "ОТМ-И", root, CaseDirection.Technical);
        var group = await divisions.CreateAsync("Группа ОТМ импорт", "ГР-И", otm, direction: null);
        var on = await divisions.CreateAsync("Отдел ОН импорт", "ОН-И", root, CaseDirection.Surveillance);

        var cases = InvestigationTestKit.CreateCaseStore(fixture.Investigation, fixture.Core);
        var head = InvestigationTestKit.Access(70, 3, group, on);
        await cases.CreateAsync(InvestigationTestKit.Draft("НОМ-7", group, 1, 70), head);
        await cases.CreateAsync(InvestigationTestKit.Draft("НОМ-7", on, 1, 70), head);
        await cases.CreateAsync(InvestigationTestKit.Draft("НОМ-8", on, 3, 70), head);

        var scope = InvestigationTestKit.CreateCaseScope(fixture.Investigation, fixture.Core);
        var found = await scope.FindCasesByNumberAsync([" НОМ-7 ", "НОМ-8", "НОМ-9"], head);
        found.Select(t => (t.Case.Number, t.DivisionCode)).OrderBy(t => t.DivisionCode).ShouldBe(
            [("НОМ-7", "ГР-И"), ("НОМ-7", "ОН-И"), ("НОМ-8", "ОН-И")]);
        found.Single(t => t.DivisionCode == "ГР-И").DepartmentLabel.ShouldBe("ОТМ — Оперативно-технические мероприятия");
        found.Single(t => t.DivisionCode == "ГР-И").DivisionName.ShouldBe("Группа ОТМ импорт");

        // Гриф ниже дела НОМ-8 и подразделение ОН вне допуска — эти дела не находятся (неотличимы от несуществующих).
        var narrow = InvestigationTestKit.Access(70, 1, group);
        (await scope.FindCasesByNumberAsync(["НОМ-7", "НОМ-8"], narrow)).Select(t => t.DivisionCode).ShouldBe(["ГР-И"]);

        // Следователь видит только свои дела (ТБ-071): чужие номера не находятся.
        (await scope.FindCasesByNumberAsync(["НОМ-7"], InvestigationTestKit.Access(71, 3, group, on))).ShouldBeEmpty();
    }

    private static readonly TimeZoneInfo PlusSix =
        TimeZoneInfo.CreateCustomTimeZone("UTC+6", TimeSpan.FromHours(6), "UTC+6", "UTC+6");

    // Исполнитель берёт задания по одному в порядке запуска, а подача в обработку общая для всех заданий: задания
    // соседних тестов класса (общая БД) останавливаются, а их носители — отмечаются обработанными, чтобы шаг и подача
    // взяли именно задание этого теста.
    private async Task StopOtherJobsAsync(int jobId)
    {
        await using var db = fixture.Media.CreateDbContext();
        await db.ImportJobs
            .Where(j => j.Id != jobId && (j.Status == MediaImportJobStatus.Queued || j.Status == MediaImportJobStatus.Running))
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, MediaImportJobStatus.Stopped));
        await db.ImportItems
            .Where(i => i.JobId != jobId && i.Processing != MediaImportProcessingState.None)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.Processing, MediaImportProcessingState.Done));
    }

    private static MediaImportItemDraft Item(
        int row, string path, long size, int caseId, short classification, int division, MediaKind kind = MediaKind.Image) =>
        new(row, path, kind, kind == MediaKind.Audio ? "audio/mpeg" : "image/jpeg", size, caseId, "N-" + caseId, classification, division,
            Captured, false, null, null, null);

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
