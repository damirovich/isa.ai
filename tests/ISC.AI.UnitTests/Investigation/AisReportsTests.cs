using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Profiles;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Domain.Enums;
using ISC.AI.Modules.DocFlow.Domain.Services;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using ISC.AI.Profile.Investigation.Application.Features.RelationGraph;
using ISC.AI.Profile.Investigation.Application.Features.Statistics;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using ISC.AI.Profile.Investigation.UI;
using Mediator;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Investigation;

/// <summary>
/// Отчётные формы ЭС3в-08, части 2–4 (ADR-0061) без БД: журнал суточной активности (видимость носителей и документов по
/// их пакетам, статусы сводок, подписи, нумерация, гриф), статистика карточек по инициаторам, отчёт по графу (порядки,
/// цепочки, пересечения, гриф по вошедшему), выгрузка с записью в журнал, правила и страницы.
/// </summary>
public sealed class AisReportsTests : BunitContext, IAsyncLifetime
{
    private const int Me = 42;
    private static readonly AccessContext Access = new("42", 2, [5, 7]);
    private static readonly DateOnly Day = new(2026, 10, 22);
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    private readonly IUserRoleStore _roles = Substitute.For<IUserRoleStore>();
    private readonly ISubjectProvider _subject = Substitute.For<ISubjectProvider>();
    private readonly IAccessContextProvider _access = Substitute.For<IAccessContextProvider>();
    private readonly IActivityReportStore _store = Substitute.For<IActivityReportStore>();
    private readonly IMediaCatalog _catalog = Substitute.For<IMediaCatalog>();
    private readonly IDocumentLookup _documents = Substitute.For<IDocumentLookup>();
    private readonly IDivisionAdminStore _divisions = Substitute.For<IDivisionAdminStore>();
    private readonly IReferenceStore _references = Substitute.For<IReferenceStore>();
    private readonly IUserDirectory _users = Substitute.For<IUserDirectory>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IReportRenderer _word = Substitute.For<IReportRenderer>();
    private readonly IMediator _mediator = Substitute.For<IMediator>();

    public AisReportsTests()
    {
        _subject.GetCurrentUserIdAsync(Arg.Any<CancellationToken>()).Returns((int?)Me);
        _roles.GetRoleAsync(Me, Arg.Any<CancellationToken>()).Returns(InvestigationRole.Head);
        _roles.AnyAdministratorAsync(Arg.Any<CancellationToken>()).Returns(true);
        _access.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(Access);
        _divisions.ListAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new DivisionNode(5, "Отдел наблюдения", null, null, true, 0, CaseDirection.Surveillance),
            new DivisionNode(7, "Отдел установки", null, null, true, 0, CaseDirection.Establishment),
            new DivisionNode(9, "Отдел ОТМ", null, null, true, 0, CaseDirection.Technical),
        ]);
        _users.ListActiveAsync(Arg.Any<CancellationToken>()).Returns([new UserItem(10, "Асанов А.")]);
        _users.GetNameAsync(11, Arg.Any<CancellationToken>()).Returns("Бекова Б. (отключена)");
        _users.GetNameAsync(Me, Arg.Any<CancellationToken>()).Returns("Руководитель Р.");

        var t = Now.UtcDateTime;
        _store.ListActivityAsync(Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<IReadOnlyCollection<int>?>(), ActivityJournalSource.MaxRows, Access, Arg.Any<CancellationToken>())
            .Returns(new ActivityEntries(
            [
                new ActivityEntry(t.AddHours(-5), ActivityMaterial.Media, 1, "Д-1", "З-101", 5, 1, 10, MediaAssetId: 100),
                new ActivityEntry(t.AddHours(-4), ActivityMaterial.Media, 1, "Д-1", "З-101", 5, 1, 10, MediaAssetId: 101), // носитель не виден
                new ActivityEntry(t.AddHours(-3), ActivityMaterial.Document, 2, "У-7", null, 7, 1, 11, DocumentId: 200),
                new ActivityEntry(t.AddHours(-2), ActivityMaterial.Document, 2, "У-7", null, 7, 1, 11, DocumentId: 201), // документ не виден
                new ActivityEntry(t.AddHours(-1), ActivityMaterial.Report, 1, "Д-1", "З-101", 5, 1, 10,
                    ReportKind: CaseReportKind.SummaryOn, ReportClassification: 2, PersonId: 77),
                new ActivityEntry(t.AddDays(-5), ActivityMaterial.Report, 1, "Д-1", "З-101", 5, 1, 10,
                    ReportKind: CaseReportKind.ReferenceUn, ReportClassification: 1),
            ],
            new Dictionary<int, string> { [1] = "Сокол, Иванов Иван" },
            new Dictionary<int, string> { [77] = "Ворон" },
            Truncated: true));
        _catalog.ListAsync(Arg.Any<IReadOnlyCollection<int>>(), Access, Arg.Any<CancellationToken>())
            .Returns([Asset(100, MediaKind.Video, 1)]);
        _documents.ResolveByIdsAsync(Arg.Any<IReadOnlyCollection<int>>(), Access, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, DocumentBrief> { [200] = new(200, "123", Day, "Рапорт", "текст", null, default, 1) });

        _store.CountObjectCardsAsync(Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<IReadOnlyCollection<int>?>(), Access, Arg.Any<CancellationToken>())
            .Returns(new ObjectCardCounts(
            [
                new ObjectCardCount(null, 2, 1, 0, 1),
                new ObjectCardCount(31, 3, 2, 1, 0),
                new ObjectCardCount(30, 1, 0, 0, 1),
            ], 2));
        _references.ListAsync(ReferenceKind.InitiatorUnit, Arg.Any<CancellationToken>()).Returns(
        [
            new ReferenceItemRow(30, ReferenceKind.InitiatorUnit, "ГУ по г. Ош", null, 0, false),
            new ReferenceItemRow(31, ReferenceKind.InitiatorUnit, "ГУ по г. Бишкек", null, 0, true),
        ]);

        _word.Format.Returns(ReportFormat.Word);
        _word.Render(Arg.Any<ReportView>()).Returns(new ReportDocument([7], "отчёт.docx", "application/docx"));

        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton(_mediator);
        Services.AddSingleton<TimeProvider>(new FixedClock(Now));
        var profile = Substitute.For<IProfile>();
        profile.DisplayName.Returns("Следствие");
        Services.AddSingleton(profile);
        SetRendererInfo(new RendererInfo("Server", true));
    }

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

    Task IAsyncLifetime.DisposeAsync() => base.DisposeAsync().AsTask();

    [Fact(DisplayName = "Журнал: невидимые носитель и документ выпадают; вид носителя из каталога; статус сводки по окну; объект — фигурант сводки или объекты дела; гриф; нумерация")]
    public async Task Activity_journal_is_enriched()
    {
        var journal = (await JournalSource().ReadAsync(Day, Day, null, CancellationToken.None)).ShouldNotBeNull();

        journal.Rows.Select(r => (r.Number, r.Material, r.Status)).ShouldBe(
        [
            (1, "видео", ActivityJournalSource.ActiveLabel),
            (2, "документ", ActivityJournalSource.ActiveLabel),
            (3, "сводка", "черновик"),   // создана час назад — окно 48 ч открыто
            (4, "справка", "архивный"),  // пять дней назад — окно закрыто
        ]);
        journal.Rows[0].Group.ShouldBe("ОН");
        journal.Rows[0].Employee.ShouldBe("Асанов А.");
        journal.Rows[0].TaskNumber.ShouldBe("З-101");
        journal.Rows[0].Objects.ShouldBe("Сокол, Иванов Иван");
        journal.Rows[1].Group.ShouldBe("ОУ");
        journal.Rows[1].Employee.ShouldBe("Бекова Б. (отключена)");
        journal.Rows[1].TaskNumber.ShouldBe("У-7");    // не задание — номер дела
        journal.Rows[1].Objects.ShouldBe("—");
        journal.Rows[2].Objects.ShouldBe("Ворон");     // у сводки — её фигурант
        journal.MaxClassification.ShouldBe((short)2);
        journal.Truncated.ShouldBeTrue();
        journal.Directions.ShouldBe([CaseDirection.Surveillance, CaseDirection.Establishment]); // ОТМ вне допуска
    }

    [Fact(DisplayName = "Журнал: группа сужает подразделения дел; без права — отказ и никаких чтений; статусы и виды словами")]
    public async Task Activity_journal_group_and_permission()
    {
        await JournalSource().ReadAsync(Day, Day, CaseDirection.Establishment, CancellationToken.None);
        await _store.Received(1).ListActivityAsync(Arg.Any<DateTime>(), Arg.Any<DateTime>(),
            Arg.Is<IReadOnlyCollection<int>?>(ids => ids != null && ids.SequenceEqual(new[] { 7 })), ActivityJournalSource.MaxRows, Access, Arg.Any<CancellationToken>());

        ActivityJournalSource.StateLabel(CaseReportState.Inactive).ShouldBe("неактивный");
        ActivityJournalSource.MaterialLabel(MediaKind.Audio).ShouldBe("аудио");

        _roles.GetRoleAsync(Me, Arg.Any<CancellationToken>()).Returns(InvestigationRole.Investigator);
        (await JournalSource().ReadAsync(Day, Day, null, CancellationToken.None)).ShouldBeNull();
        (await new ActivityJournalQuery.Handler(JournalSource()).Handle(new ActivityJournalQuery(Day, Day), CancellationToken.None))
            .StatusMessage.ShouldBe(StatisticsReports.Denied);
    }

    [Fact(DisplayName = "Журнал — выгрузка: колонки формы, итоги по видам, пометка о пределе, «Сформировал»; запись «Выгрузка» с грифом до файла")]
    public async Task Activity_journal_export()
    {
        ReportView? view = null;
        _word.Render(Arg.Do<ReportView>(v => view = v)).Returns(new ReportDocument([7], "журнал.docx", "application/docx"));
        var handler = new ExportActivityJournalCommand.Handler(JournalSource(), [_word], _subject, _users, _audit, new FixedClock(Now));

        var file = (await handler.Handle(new ExportActivityJournalCommand(Day, Day, null, ReportFormat.Word), CancellationToken.None)).Data.ShouldNotBeNull();

        file.Content.ShouldBe([7]);
        view.ShouldNotBeNull();
        view.Columns.ShouldBe(ActivityJournalBuilder.Columns);
        view.Groups.ShouldHaveSingleItem().Rows.Count.ShouldBe(4);
        view.Totals[0].ShouldBe(new ReportTotal("Всего записей", "4"));
        view.FilterLines.ShouldContain(l => l.Contains("показаны первые", StringComparison.Ordinal));
        view.PrintStamp.ShouldStartWith("Сформировал: Руководитель Р.");
        view.ClassificationMarking.ShouldBe(ClassificationLevels.Marking(2));
        await _audit.Received(1).WriteAsync(
            Arg.Is<AuditEntry>(e => e.Action == AuditAction.Export && e.Classification == 2
                && e.ObjectRef!.StartsWith("investigation:reports:activity", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Карточки объектов: инициаторы по справочнику (и выключенные), «не указан» — последним, итог и гриф")]
    public async Task Object_cards_by_initiator()
    {
        var report = (await CardSource().ReadAsync(new DateOnly(2026, 1, 1), Day, null, CancellationToken.None)).ShouldNotBeNull();

        report.Rows.Select(r => r.Initiator).ShouldBe(["ГУ по г. Бишкек", "ГУ по г. Ош", ObjectCardReportSource.NoInitiator]);
        (report.Total.Total, report.Total.InProgress, report.Total.Suspended, report.Total.Closed).ShouldBe((6, 3, 1, 2));
        report.MaxClassification.ShouldBe((short)2);
        ObjectCardReportBuilder.Cells(report.Rows[0].Count).ShouldBe(["3", "2", "1", "0"]);
        new ObjectCardReportQuery(Day, Day).AuditSummary.ShouldBe("investigation:reports:object-cards:view");
    }

    [Fact(DisplayName = "Отчёт по графу: порядок — по числу фигурантов на пути; дела и реквизиты — звенья; цепочки 2-го и 3-го; 4-й — нет; пересечения; гриф по вошедшему")]
    public void Graph_report_orders_and_chains()
    {
        var report = RelationGraphReportBuilder.Build(SampleGraph(), "person:1");

        report.Center.Title.ShouldBe("Исходов И.");
        report.Cases.Select(c => c.Title).ShouldBe(["Дело № 5"]);
        report.Persons.Select(p => (p.Node.Title, p.Order)).ShouldBe(
        [
            ("Брат Б.", 1),
            ("Соделец С.", 1),
            ("Второй В.", 2),
            ("Третий Т.", 3),
        ]);
        report.Persons.Single(p => p.Node.Title == "Соделец С.").Via.ShouldBe("дело: Дело № 5");
        report.Persons.Single(p => p.Node.Title == "Брат Б.").Via.ShouldBe("брат");
        report.Persons.Single(p => p.Node.Title == "Второй В.").Via.ShouldBe("Исходов И. → Дело № 5 → Соделец С. → (друг) Второй В.");
        report.Persons.Single(p => p.Node.Title == "Третий Т.").Via.ShouldBe(
            "Исходов И. → Дело № 5 → Соделец С. → (друг) Второй В. → 01KG777AAA → Третий Т.");
        report.Intersections.ShouldHaveSingleItem().Shared.ShouldBe("Второй В., Дело № 9 (другого подразделения), Третий Т.");
        report.MaxClassification.ShouldBe((short)2); // «Четвёртый» с грифом 3 в отчёт не вошёл

        var view = RelationGraphReportBuilder.ToView(report, "Руководитель Р.", Now.UtcDateTime);
        view.Groups.Select(g => g.Title).ShouldBe(
            ["Исходный объект", "Дела и карточки", "Объекты 1-го порядка", "Цепочки 2-го порядка", "Цепочки 3-го порядка",
             "Пересечения по транспорту, адресам и телефонам"]);
        view.Totals.Single(t => t.Label == "Объектов 3-го порядка").Value.ShouldBe("1");
    }

    [Fact(DisplayName = "Правила: журнал — период не длиннее года; карточки — пять лет; отчёт по графу — фигурант и формат")]
    public void Validators()
    {
        new ActivityJournalValidator().Validate(new ActivityJournalQuery(Day.AddDays(-400), Day)).IsValid.ShouldBeFalse();
        new ActivityJournalValidator().Validate(new ActivityJournalQuery(Day, Day, CaseDirection.Technical)).IsValid.ShouldBeTrue();
        new ExportActivityJournalValidator().Validate(new ExportActivityJournalCommand(Day, Day, null, (ReportFormat)9)).IsValid.ShouldBeFalse();
        new ObjectCardReportValidator().Validate(new ObjectCardReportQuery(Day.AddYears(-2), Day)).IsValid.ShouldBeTrue();
        new ExportObjectCardReportValidator().Validate(new ExportObjectCardReportCommand(Day, Day.AddDays(-1), null, ReportFormat.Pdf)).IsValid.ShouldBeFalse();
        new ExportRelationGraphReportValidator().Validate(new ExportRelationGraphReportCommand(0, ReportFormat.Word)).IsValid.ShouldBeFalse();
        new ExportRelationGraphReportValidator().Validate(new ExportRelationGraphReportCommand(3, ReportFormat.Word)).IsValid.ShouldBeTrue();
    }

    [Fact(DisplayName = "Страница журнала: по умолчанию — сегодня, строки формы и гриф; «Word» — выгрузка файлом")]
    public async Task Journal_page()
    {
        var journal = (await JournalSource().ReadAsync(Day, Day, null, CancellationToken.None))!;
        _mediator.Send(Arg.Any<ActivityJournalQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<ActivityJournal>>(ResponseDto<ActivityJournal>.Ok(journal)));
        _mediator.Send(Arg.Any<ExportActivityJournalCommand>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<ReportDocument>>(ResponseDto<ReportDocument>.Ok(new ReportDocument([1], "журнал.docx", "x"))));
        Render<MudPopoverProvider>();

        var cut = Render<ActivityJournalPage>();

        cut.WaitForAssertion(() => cut.Markup.ShouldContain("Сокол, Иванов Иван"));
        _mediator.Received(1).Send(Arg.Is<ActivityJournalQuery>(q => q.From == Day && q.To == Day && q.Direction == null), Arg.Any<CancellationToken>());
        cut.Markup.ShouldContain("показаны первые по времени");
        cut.Markup.ShouldContain("гриф журнала");
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Word").Click();
        cut.WaitForAssertion(() => JSInterop.Invocations.Select(i => i.Identifier).ShouldContain("downloadFileFromStream"));
    }

    [Fact(DisplayName = "Страница статистики карточек: по умолчанию — с начала года, строки инициаторов и итог")]
    public async Task Object_cards_page()
    {
        var report = (await CardSource().ReadAsync(new DateOnly(2026, 1, 1), Day, null, CancellationToken.None))!;
        _mediator.Send(Arg.Any<ObjectCardReportQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<ObjectCardReport>>(ResponseDto<ObjectCardReport>.Ok(report)));
        Render<MudPopoverProvider>();

        var cut = Render<ObjectCardReportPage>();

        cut.WaitForAssertion(() => cut.Markup.ShouldContain("ГУ по г. Бишкек"));
        _mediator.Received(1).Send(Arg.Is<ObjectCardReportQuery>(q => q.From == new DateOnly(2026, 1, 1) && q.To == Day), Arg.Any<CancellationToken>());
        cut.Markup.ShouldContain("Итого");
        cut.Markup.ShouldContain(ObjectCardReportSource.NoInitiator);
    }

    // Исходов —дело 5— Соделец —друг— Второй —а/м— Третий; Исходов —брат— Брат; Третий —связь— Четвёртый (4-й порядок,
    // гриф 3); автомобиль 01KG777AAA общий у Второго и Третьего и совпал с чужим делом № 9; адрес — только у Исходова.
    private static RelationGraph SampleGraph()
    {
        RelationNode Node(string key, RelationNodeKind kind, string title, short classification = 1) =>
            new(key, kind, null, title, null, 0, null, true, Classification: classification);
        return new RelationGraph(
        [
            Node("person:1", RelationNodeKind.Person, "Исходов И."),
            Node("case:5", RelationNodeKind.Case, "Дело № 5", 2),
            Node("person:2", RelationNodeKind.Person, "Соделец С."),
            Node("person:3", RelationNodeKind.Person, "Второй В."),
            Node("vehicle:01KG777AAA", RelationNodeKind.Vehicle, "01KG777AAA"),
            Node("person:4", RelationNodeKind.Person, "Третий Т."),
            Node("person:5", RelationNodeKind.Person, "Брат Б."),
            Node("person:6", RelationNodeKind.Person, "Четвёртый Ч.", 3),
            Node("foreign:9", RelationNodeKind.ForeignCase, "Дело № 9 (другого подразделения)"),
            Node("address:x", RelationNodeKind.Address, "г. Ош, ул. Ленина, 1"),
        ],
        [
            new RelationEdge("person:1", "case:5", RelationEdgeKind.CaseMember, "объект"),
            new RelationEdge("person:2", "case:5", RelationEdgeKind.CaseMember, "связь"),
            new RelationEdge("person:2", "person:3", RelationEdgeKind.PersonLink, "друг"),
            new RelationEdge("person:3", "vehicle:01KG777AAA", RelationEdgeKind.Requisite, "автотранспорт"),
            new RelationEdge("person:4", "vehicle:01KG777AAA", RelationEdgeKind.Requisite, "автотранспорт"),
            new RelationEdge("vehicle:01KG777AAA", "foreign:9", RelationEdgeKind.Intersection, "пересечение: госномер"),
            new RelationEdge("person:1", "person:5", RelationEdgeKind.PersonLink, "брат"),
            new RelationEdge("person:4", "person:6", RelationEdgeKind.PersonLink, "сосед"),
            new RelationEdge("person:1", "address:x", RelationEdgeKind.Requisite, "проживание"),
        ],
        Truncated: false);
    }

    private ActivityJournalSource JournalSource() =>
        new(_roles, _subject, _access, _store, _catalog, _documents, _divisions, _users, new CaseReportOptions(), new FixedClock(Now));

    private ObjectCardReportSource CardSource() =>
        new(_roles, _subject, _access, _store, _references, _divisions, new FixedClock(Now));

    private static MediaAssetRow Asset(int id, MediaKind kind, short classification) => new(
        id, kind, "v.mp4", "v.mp4", "video/mp4", 1, null, null, null, classification, 5, 1, MediaIndexStatus.Indexed,
        null, null, null, null, DateTime.UtcNow, 0);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
