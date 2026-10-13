using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.BackgroundTasks;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Application;
using ISC.AI.Modules.Media.Application.Features.Import;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Media.Import;

/// <summary>
/// Документы архива в импорте (ADR-0042): проверка описи (тип, дата, название), приём через порт профиля, повтор без
/// второго документа, продолжение прерванной попытки, сверка сигнатуры и контрольной суммы, подача индексации порциями.
/// </summary>
public sealed class ArchiveDocumentImportTests : IDisposable
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("UTC+6", TimeSpan.FromHours(6), "UTC+6", "UTC+6");
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly AccessContext Access = new("7", 3, [1]);
    private static readonly byte[] Docx = [.. "PK\u0003\u0004"u8, 1, 2, 3, 4, 5];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "iscai-docimport-test-" + Guid.NewGuid().ToString("N"));
    private readonly ICaseScope _caseScope = Substitute.For<ICaseScope>();
    private readonly IArchiveDocumentSink _sink = Substitute.For<IArchiveDocumentSink>();
    private readonly IMediaImportStore _imports = Substitute.For<IMediaImportStore>();

    public ArchiveDocumentImportTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "дело 1"));
        File.WriteAllBytes(Path.Combine(_root, "дело 1", "сводка.docx"), Docx);
        File.WriteAllBytes(Path.Combine(_root, "дело 1", "справка.pdf"), [.. "%PDF-1.4"u8, 9]);
        File.WriteAllBytes(Path.Combine(_root, "дело 1", "подмена.doc"), [.. "MZ"u8, 0, 0]);
        File.SetLastWriteTimeUtc(Path.Combine(_root, "дело 1", "справка.pdf"), new DateTime(2021, 3, 4, 20, 0, 0, DateTimeKind.Utc));

        _sink.IsSupported.Returns(true);
        _sink.ListTypesAsync(Arg.Any<CancellationToken>()).Returns([new ArchiveDocumentType(5, "Сводка ОН"), new ArchiveDocumentType(6, "Справка")]);
        _caseScope.FindCasesByNumberAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>())
            .Returns([new CaseImportTarget(new CaseScopeItem(10, "1/2023", "Дело", 2, 1), "Отдел", "ОН1", "ОН — Оперативное наблюдение")]);
        _caseScope.GetCaseAsync(10, Access, Arg.Any<CancellationToken>()).Returns(new CaseScopeItem(10, "1/2023", "Дело", 2, 1));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact(DisplayName = "Опись: тип по наименованию (без учёта регистра и «ё»), дата документа, название; без даты — дата файла, без названия — имя файла")]
    public async Task Plan_reads_document_requisites()
    {
        var plan = await PlanAsync(null,
            "файл;дело;тип документа;рег. номер;дата документа;название",
            "дело 1/сводка.docx;1/2023;сводка он;12-с;14.05.2023;Сводка за 14 мая",
            "дело 1/справка.pdf;1/2023;Справка;;;");

        plan.Problems.ShouldBeEmpty();
        plan.DocumentCount.ShouldBe(2);
        plan.Cases.ShouldHaveSingleItem().DocumentCount.ShouldBe(2);

        var first = plan.Items[0];
        first.Kind.ShouldBe(MediaImportFileKind.Document);
        first.ContentType.ShouldBe(ArchiveDocumentFormats.DocxContentType);
        first.CapturedAt.ShouldBeNull();
        first.Document.ShouldBe(new MediaImportDocumentDraft(5, "12-с", new DateOnly(2023, 5, 14), "Сводка за 14 мая", false));

        var second = plan.Items[1].Document.ShouldNotBeNull();
        second.TypeId.ShouldBe(6);
        second.Title.ShouldBe("справка");
        second.RegDate.ShouldBe(new DateOnly(2021, 3, 5), "дата изменения файла в поясе +6");
        second.RegDateFromFile.ShouldBeTrue();
        plan.DocumentDateFromFileCount.ShouldBe(1);
    }

    [Fact(DisplayName = "Опись: тип не указан — общий тип задания; без него и с неизвестным типом — ошибка строки")]
    public async Task Plan_document_type_rules()
    {
        var withDefault = await PlanAsync(6, "файл;дело", "дело 1/сводка.docx;1/2023");
        withDefault.Items.ShouldHaveSingleItem().Document!.TypeId.ShouldBe(6);

        var withoutDefault = await PlanAsync(null, "файл;дело;тип документа", "дело 1/сводка.docx;1/2023;", "дело 1/справка.pdf;1/2023;Приказ");
        withoutDefault.Items.ShouldBeEmpty();
        withoutDefault.Problems[0].Message.ShouldContain("укажите тип документа");
        withoutDefault.Problems[1].Message.ShouldContain("тип документа «Приказ» не найден");
    }

    [Fact(DisplayName = "Опись: экземпляр без документооборота — строка-документ получает отказ, носители принимаются")]
    public async Task Plan_rejects_documents_without_sink()
    {
        var manifest = ImportManifest.Read(Encoding.UTF8.GetBytes("файл;дело\nдело 1/сводка.docx;1/2023\n"));
        var plan = await new MediaImportPlanner(_caseScope, null, new UnsupportedArchiveDocumentSink())
            .PlanAsync(_root, "опись.csv", manifest, Access, Zone, Now);

        plan.Problems.ShouldHaveSingleItem().Message.ShouldContain(UnsupportedArchiveDocumentSink.NotSupportedMessage);
    }

    [Fact(DisplayName = "Приём документа: файл, тип, номер, дата, название и происхождение уходят профилю под допуском оператора; номер документа пишется в строку сразу")]
    public async Task Runner_imports_document_through_sink()
    {
        _sink.ImportAsync(Arg.Any<ArchiveDocumentRequest>(), Access, Arg.Any<Func<int, Task>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                await call.Arg<Func<int, Task>>()(77);
                return new ArchiveDocumentResult(77, null);
            });
        var work = Work(document: new MediaImportDocumentDraft(5, "12-с", new DateOnly(2023, 5, 14), "Сводка", false), expectedSha: Sha(Docx));

        (await Runner().RunItemAsync(Context(work))).ShouldBe(MediaImportStep.Processed);

        await _sink.Received(1).ImportAsync(
            Arg.Is<ArchiveDocumentRequest>(r => r.Case.CaseId == 10 && r.Case.Classification == 2 && r.FileName == "сводка.docx"
                && r.Content.SequenceEqual(Docx) && r.TypeId == 5 && r.RegNumber == "12-с" && r.RegDate == new DateOnly(2023, 5, 14)
                && r.Title == "Сводка" && r.Notes.Contains("задание 5, строка описи 2") && r.ExistingDocumentId == null),
            Access, Arg.Any<Func<int, Task>>(), Arg.Any<CancellationToken>());
        await _imports.Received(1).SetItemDocumentAsync(100, 77, Arg.Any<CancellationToken>());
        await _imports.Received(1).CompleteItemAsync(100, Arg.Is<MediaImportItemOutcome>(o => o.Status == MediaImportItemStatus.Accepted
            && o.DocumentId == 77 && o.ContentSha256 == Sha(Docx) && o.Processing == MediaImportProcessingState.Waiting), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Тот же файл уже введён импортом в это дело — «дубликат» без второго документа")]
    public async Task Runner_skips_already_imported_document()
    {
        _imports.FindImportedDocumentAsync(10, Sha(Docx), Arg.Any<CancellationToken>()).Returns(33);

        await Runner().RunItemAsync(Context(Work()));

        await _sink.DidNotReceiveWithAnyArgs().ImportAsync(default!, default!, default!);
        await _imports.Received(1).CompleteItemAsync(100, Arg.Is<MediaImportItemOutcome>(o => o.Status == MediaImportItemStatus.Duplicate
            && o.DocumentId == 33 && o.Processing == MediaImportProcessingState.None), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Прерванная попытка (номер документа уже в строке) — продолжается тот же документ, не дубликат")]
    public async Task Runner_resumes_interrupted_document()
    {
        _imports.FindImportedDocumentAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(33);
        _sink.ImportAsync(Arg.Any<ArchiveDocumentRequest>(), Access, Arg.Any<Func<int, Task>>(), Arg.Any<CancellationToken>())
            .Returns(new ArchiveDocumentResult(55, null));

        await Runner().RunItemAsync(Context(Work(), documentId: 55));

        await _imports.DidNotReceiveWithAnyArgs().FindImportedDocumentAsync(default, default!);
        await _sink.Received(1).ImportAsync(Arg.Is<ArchiveDocumentRequest>(r => r.ExistingDocumentId == 55), Access, Arg.Any<Func<int, Task>>(), Arg.Any<CancellationToken>());
    }

    [Theory(DisplayName = "Подмена формата и неверная контрольная сумма — отказ, документ не регистрируется")]
    [InlineData("signature")]
    [InlineData("checksum")]
    public async Task Runner_rejects_bad_files(string kind)
    {
        var work = kind == "signature"
            ? Work(path: "дело 1/подмена.doc", contentType: ArchiveDocumentFormats.DocContentType)
            : Work(expectedSha: new string('0', 64));

        await Runner().RunItemAsync(Context(work));

        await _sink.DidNotReceiveWithAnyArgs().ImportAsync(default!, default!, default!);
        await _imports.Received(1).CompleteItemAsync(100, Arg.Is<MediaImportItemOutcome>(o => o.Status == MediaImportItemStatus.Rejected
            && o.Reason!.Contains(kind == "signature" ? ArchiveDocumentFormats.SignatureMismatchMessage : "Контрольная сумма не совпала")),
            Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Отказ профиля (например, занятый рег. номер) — отказ строки с причиной")]
    public async Task Runner_reports_sink_refusal()
    {
        _sink.ImportAsync(Arg.Any<ArchiveDocumentRequest>(), Access, Arg.Any<Func<int, Task>>(), Arg.Any<CancellationToken>())
            .Returns(ArchiveDocumentResult.Fail("документ с таким рег. номером уже зарегистрирован"));

        await Runner().RunItemAsync(Context(Work()));

        await _imports.Received(1).CompleteItemAsync(100, Arg.Is<MediaImportItemOutcome>(o => o.Status == MediaImportItemStatus.Rejected
            && o.Reason == "документ с таким рег. номером уже зарегистрирован"), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Подача: документ, ждущий очереди, — индексация текста через профиль, задача в строке")]
    public async Task Feeder_enqueues_document_indexing()
    {
        var task = Guid.NewGuid();
        _imports.ListQueuedProcessingAsync(Arg.Any<CancellationToken>()).Returns([]);
        _imports.ListWaitingProcessingAsync(1, Arg.Any<CancellationToken>()).Returns([new MediaImportFeedItem(9, null, null, 77, null, null)]);
        _sink.EnqueueIndexingAsync(77, Arg.Any<CancellationToken>()).Returns(task);

        var feeder = new MediaImportFeeder(
            _imports, Substitute.For<IMediaStore>(), Substitute.For<IBackgroundTaskQueue>(), Substitute.For<IBackgroundTaskStore>(),
            new MediaImportOptions([], 1), _sink);
        (await feeder.FeedAsync()).ShouldBe(1);

        await _imports.Received(1).MarkProcessingQueuedAsync(9, task, null, Arg.Any<CancellationToken>());
    }

    [Theory(DisplayName = "Сигнатуры документов: .docx — ZIP, .doc — OLE или RTF, .pdf — %PDF")]
    [InlineData(ArchiveDocumentFormats.DocxContentType, new byte[] { 0x50, 0x4B, 0x03, 0x04, 0 }, true)]
    [InlineData(ArchiveDocumentFormats.DocContentType, new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }, true)]
    [InlineData(ArchiveDocumentFormats.DocContentType, new byte[] { 0x7B, 0x5C, 0x72, 0x74, 0x66, 0x31 }, true)]
    [InlineData(ArchiveDocumentFormats.PdfContentType, new byte[] { 0x25, 0x50, 0x44, 0x46 }, true)]
    [InlineData(ArchiveDocumentFormats.PdfContentType, new byte[] { 0x50, 0x4B, 0x03, 0x04 }, false)]
    [InlineData(ArchiveDocumentFormats.DocContentType, new byte[] { 0x4D, 0x5A }, false)]
    public void Signatures(string contentType, byte[] head, bool expected) =>
        ArchiveDocumentFormats.MatchesSignature(contentType, head).ShouldBe(expected);

    private async Task<MediaImportPlan> PlanAsync(int? defaultType, params string[] lines)
    {
        var manifest = ImportManifest.Read(Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n"));
        manifest.Error.ShouldBeNull();
        return await new MediaImportPlanner(_caseScope, null, _sink).PlanAsync(_root, "опись.csv", manifest, Access, Zone, Now, defaultType);
    }

    private MediaImportRunner Runner()
    {
        var administration = Substitute.For<IMediaAdministration>();
        administration.UserCanImportArchiveAsync(7, Arg.Any<CancellationToken>()).Returns(true);
        return new MediaImportRunner(
            _imports, administration, Substitute.For<IUserAccessReader>(), _caseScope, Substitute.For<IMediaStore>(),
            Substitute.For<IAuditWriter>(), new MediaImportOptions([new MediaImportRoot(_root)], 1), MediaUploadOptions.Default,
            TimeProvider.System, _sink);
    }

    private MediaImportContext Context(MediaImportWorkItem work)
    {
        _imports.TakeNextItemAsync(5, Arg.Any<CancellationToken>()).Returns(work);
        return new MediaImportContext(new MediaImportJobRun(5, _root, 7), Access);
    }

    private MediaImportContext Context(MediaImportWorkItem work, int documentId) => Context(work with { DocumentId = documentId });

    private static MediaImportWorkItem Work(
        MediaImportDocumentDraft? document = null, string? expectedSha = null, string path = "дело 1/сводка.docx",
        string contentType = ArchiveDocumentFormats.DocxContentType) =>
        new(100, 5, new MediaImportItemDraft(
            2, path, MediaImportFileKind.Document, contentType, Docx.Length, 10, "1/2023", 2, 1, null, false, "архив ОН", null,
            expectedSha, document ?? new MediaImportDocumentDraft(5, null, new DateOnly(2023, 5, 14), "Сводка", false)));

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
}
