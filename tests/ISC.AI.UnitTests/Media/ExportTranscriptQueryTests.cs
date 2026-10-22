using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Documents;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Application.Features.Transcripts;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Media;

/// <summary>
/// Выгрузка расшифровки (ADR-0047, ТФ-МЕД-18): запись «Выгрузка» в журнале с носителем и форматом, без текста (ТБ-032);
/// только носитель своего дела — кандидат поиска (только просмотр, ADR-0044) и чужое — отказ; без фрагментов и без
/// сотрудника — отказ; Word — через порт ядра с грифом носителя, исполнителем и пометкой «требует проверки» (ТБ-033,
/// ТЭ-002); текст — маркировка грифа первой и последней строкой; фрагменты дословно, по одной строке с таймкодом.
/// </summary>
public sealed class ExportTranscriptQueryTests
{
    private const int AssetId = 5;
    private static readonly AccessContext Access = new("42", 2, [7]);

    private readonly IAccessContextProvider _accessProvider = Substitute.For<IAccessContextProvider>();
    private readonly IMediaCatalog _catalog = Substitute.For<IMediaCatalog>();
    private readonly ICaseScope _caseScope = Substitute.For<ICaseScope>();
    private readonly ISearchSessionStore _sessions = Substitute.For<ISearchSessionStore>();
    private readonly ISubjectProvider _subject = Substitute.For<ISubjectProvider>();
    private readonly IUserAccountStore _accounts = Substitute.For<IUserAccountStore>();
    private readonly IDocumentExporter _exporter = Substitute.For<IDocumentExporter>();
    private readonly FixedTime _time = new(new DateTimeOffset(2026, 10, 15, 9, 30, 0, TimeSpan.Zero));

    public ExportTranscriptQueryTests()
    {
        _accessProvider.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(Access);
        _caseScope.IsAssetAccessibleAsync(AssetId, Access, Arg.Any<CancellationToken>()).Returns(true);
        _caseScope.GetCaseIdForAssetAsync(AssetId, Access, Arg.Any<CancellationToken>()).Returns(3);
        _caseScope.GetCaseAsync(3, Access, Arg.Any<CancellationToken>()).Returns(new CaseScopeItem(3, "14/24", "Дело", 1, 7));
        _catalog.GetAsync(AssetId, Access, Arg.Any<CancellationToken>()).Returns(Asset());
        _catalog.GetTranscriptAsync(AssetId, Access, Arg.Any<CancellationToken>()).Returns(Transcript(
            new TranscriptSegmentRow(1, 65_000, 70_000, "экинчи\nбөлүк"),
            new TranscriptSegmentRow(0, 1_000, 4_000, "салам")));
        _subject.GetCurrentUserIdAsync(Arg.Any<CancellationToken>()).Returns(42);
        _accounts.ListAsync(Arg.Any<CancellationToken>()).Returns(
            [new UserAccountRow(42, "petrov", "Петров П.П.", IsActive: true, HasLocalPassword: true, MustChangePassword: false)]);
        _exporter.ExportToDocxAsync(Arg.Any<DocumentExportRequest>(), Arg.Any<CancellationToken>()).Returns([1, 2, 3]);
    }

    [Fact(DisplayName = "ТБ-030/032: выгрузка — запись «Выгрузка» с носителем и форматом, без текста расшифровки")]
    public void Audit_summary_has_asset_and_format_only()
    {
        var docx = new ExportTranscriptQuery(AssetId, TranscriptExportFormat.Docx);
        docx.AuditAction.ShouldBe(AuditAction.Export);
        docx.AuditSummary.ShouldBe("media:asset:5:transcript:export;format=docx");
        new ExportTranscriptQuery(AssetId, TranscriptExportFormat.Text).AuditSummary
            .ShouldBe("media:asset:5:transcript:export;format=text");
    }

    [Fact(DisplayName = "Word: порт ядра получает гриф носителя, заголовок, пометку «требует проверки», исполнителя и фрагменты по порядку с таймкодами")]
    public async Task Docx_goes_through_core_exporter_with_requisites()
    {
        DocumentExportRequest? request = null;
        _exporter.ExportToDocxAsync(Arg.Do<DocumentExportRequest>(r => request = r), Arg.Any<CancellationToken>()).Returns([9, 9]);

        var export = (await HandleAsync(TranscriptExportFormat.Docx)).Data.ShouldNotBeNull();

        export.Content.ShouldBe(new byte[] { 9, 9 });
        export.FileName.ShouldBe("Расшифровка носителя 5.docx");
        export.ContentType.ShouldBe("application/vnd.openxmlformats-officedocument.wordprocessingml.document");
        request.ShouldNotBeNull();
        request.Title.ShouldBe(TranscriptExportText.Title);
        request.ClassificationMarking.ShouldBe(ClassificationLevels.Marking(2));
        request.DraftNotice.ShouldBe(TranscriptExportText.Notice);
        request.Executor.ShouldBe("Петров П.П.");
        request.Reference.ShouldBeNull(); // учётный номер — «не присвоен»: его ставит регистрация документа, не выгрузка
        var lines = request.Body.Split('\n');
        lines.ShouldContain("Носитель № 5: допрос.mp3");
        lines.ShouldContain("Дело: № 14/24");
        lines.ShouldContain("Длительность записи: 01:10");
        lines.ShouldContain("Выгружено: 15.10.2026 09:30");
        lines[^2].ShouldBe("[00:01] салам");
        lines[^1].ShouldBe("[01:05] экинчи бөлүк"); // перевод строки внутри фрагмента — пробел: пара «таймкод — текст» цела
    }

    [Fact(DisplayName = "Текст для буфера: маркировка грифа первой и последней строкой, пометка, исполнитель, строки CRLF")]
    public async Task Text_is_marked_first_and_last()
    {
        var export = (await HandleAsync(TranscriptExportFormat.Text)).Data.ShouldNotBeNull();

        var text = export.Text.ShouldNotBeNull();
        var lines = text.Split("\r\n");
        lines[0].ShouldBe("СЕКРЕТНО");
        lines[1].ShouldBe(TranscriptExportText.Title);
        lines[2].ShouldBe(TranscriptExportText.Notice);
        lines.ShouldContain("[00:01] салам");
        lines[^2].ShouldBe("Исполнитель: Петров П.П.");
        lines[^1].ShouldBe("СЕКРЕТНО");
        export.Content.ShouldBeEmpty();
        await _exporter.DidNotReceiveWithAnyArgs().ExportToDocxAsync(default!, default);
    }

    [Fact(DisplayName = "ADR-0044: кандидат взаимного поиска (только просмотр) — отказ; чужой носитель — «не найден», каталог не читается")]
    public async Task View_only_and_foreign_are_refused()
    {
        _caseScope.IsAssetAccessibleAsync(AssetId, Access, Arg.Any<CancellationToken>()).Returns(false);
        _sessions.ListCaseIdsWithCandidateAssetAsync(AssetId, Access, Arg.Any<CancellationToken>()).Returns([3]);

        (await HandleAsync(TranscriptExportFormat.Docx)).StatusMessage.ShouldBe(ExportTranscriptQuery.ViewOnlyMessage);

        _sessions.ListCaseIdsWithCandidateAssetAsync(AssetId, Access, Arg.Any<CancellationToken>()).Returns([]);
        (await HandleAsync(TranscriptExportFormat.Docx)).StatusMessage.ShouldBe(ExportTranscriptQuery.NotFoundMessage);

        await _catalog.DidNotReceiveWithAnyArgs().GetTranscriptAsync(default, default!, default);
        await _exporter.DidNotReceiveWithAnyArgs().ExportToDocxAsync(default!, default);
    }

    [Fact(DisplayName = "Без фрагментов — «выгружать нечего»; без определённого сотрудника — отказ: исполнитель обязателен (ТБ-033)")]
    public async Task Empty_and_no_subject_are_refused()
    {
        _subject.GetCurrentUserIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        (await HandleAsync(TranscriptExportFormat.Text)).StatusMessage.ShouldBe(ExportTranscriptQuery.NoSubjectMessage);

        _catalog.GetTranscriptAsync(AssetId, Access, Arg.Any<CancellationToken>()).Returns(Transcript());
        (await HandleAsync(TranscriptExportFormat.Text)).StatusMessage.ShouldBe(ExportTranscriptQuery.EmptyMessage);
    }

    [Fact(DisplayName = "Носитель вне решётки (каталог не вернул) — «не найден», как у карточки")]
    public async Task Outside_lattice_is_not_found()
    {
        _catalog.GetAsync(AssetId, Access, Arg.Any<CancellationToken>()).Returns((MediaAssetRow?)null);

        (await HandleAsync(TranscriptExportFormat.Docx)).StatusMessage.ShouldBe(ExportTranscriptQuery.NotFoundMessage);
    }

    [Fact(DisplayName = "ADR-0062: с дикторами — выгрузка по репликам «Диктор [таймкод]: текст», подряд идущие фрагменты одного диктора склеены")]
    public async Task Speakers_export_by_replicas()
    {
        _catalog.GetTranscriptAsync(AssetId, Access, Arg.Any<CancellationToken>()).Returns(Transcript(
            new TranscriptSegmentRow(0, 1_000, 2_000, "салам", Speaker: 1, SpeakerLabel: "объект"),
            new TranscriptSegmentRow(1, 2_000, 3_000, "кандайсың", Speaker: 1, SpeakerLabel: "объект"),
            new TranscriptSegmentRow(2, 65_000, 70_000, "жакшы", Speaker: 2, SpeakerLabel: "Диктор 2"),
            new TranscriptSegmentRow(3, 71_000, 72_000, "шум")));

        var lines = (await HandleAsync(TranscriptExportFormat.Text)).Data.ShouldNotBeNull().Text.ShouldNotBeNull().Split("\r\n");

        lines.ShouldContain("объект [00:01]: салам кандайсың");
        lines.ShouldContain("Диктор 2 [01:05]: жакшы");
        lines.ShouldContain("Диктор не определён [01:11]: шум");
    }

    [Fact(DisplayName = "ADR-0062: склейка реплик — только подряд идущие фрагменты одного диктора; без диктора — каждый фрагмент отдельно")]
    public void Replicas_join_only_consecutive_same_speaker()
    {
        var replicas = TranscriptExportText.Replicas(
        [
            new TranscriptSegmentRow(0, 0, 1, "а", Speaker: 1, SpeakerLabel: "Диктор 1"),
            new TranscriptSegmentRow(1, 1, 2, "б", Speaker: 2, SpeakerLabel: "Диктор 2"),
            new TranscriptSegmentRow(2, 2, 3, "в", Speaker: 1, SpeakerLabel: "Диктор 1"),
            new TranscriptSegmentRow(3, 3, 4, "г"),
            new TranscriptSegmentRow(4, 4, 5, "д"),
        ]);

        replicas.Select(r => (r.Label, string.Join(" ", r.Texts))).ShouldBe(
            [("Диктор 1", "а"), ("Диктор 2", "б"), ("Диктор 1", "в"), (null, "г"), (null, "д")]);
    }

    [Theory(DisplayName = "Таймкод выгрузки: мм:сс, с часа — ч:мм:сс (как в карточке)")]
    [InlineData(0L, "00:00")]
    [InlineData(65_000L, "01:05")]
    [InlineData(3_725_000L, "1:02:05")]
    public void Clock_format(long ms, string expected) => TranscriptExportText.Clock(ms).ShouldBe(expected);

    private async Task<ISC.AI.Abstractions.Application.ResponseDto<TranscriptExport>> HandleAsync(TranscriptExportFormat format) =>
        await new ExportTranscriptQuery.Handler(_accessProvider, _catalog, _caseScope, _sessions, _subject, _accounts, _exporter, _time)
            .Handle(new ExportTranscriptQuery(AssetId, format), CancellationToken.None);

    private static MediaAssetRow Asset() => new(
        AssetId, MediaKind.Audio, "допрос.mp3", "0123456789abcdef0123456789abcdef.mp3", "audio/mpeg", 1024, 70_000, null, null,
        2, 7, 1, MediaIndexStatus.NotApplicable, null, null, null, null, DateTime.UtcNow, 0, TranscriptStatus.Done);

    /// <summary>Часы с заданным «сейчас» в поясе UTC — время выгрузки в документе предсказуемо.</summary>
    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private static MediaTranscript Transcript(params TranscriptSegmentRow[] segments) =>
        new(AssetId, TranscriptStatus.Done, null, "gigaam-v3@1", new DateTime(2026, 10, 14, 8, 0, 0, DateTimeKind.Utc), segments);
}
