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
using ISC.AI.Modules.DocFlow.Domain.Services;
using ISC.AI.Modules.Media.Domain.Services;
using ISC.AI.Profile.Investigation.Application.Features.Statistics;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using ISC.AI.Profile.Investigation.UI;
using Mediator;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Investigation;

/// <summary>
/// Отчёт результативности распознавания (ТФ-ОТЧ-07, [АИС 6.5], ADR-0061) без БД: наименования подразделений с отделом,
/// период по местному времени → UTC, ячейки и доля отбраковки, представление для выгрузки с грифом, право «Статистические
/// отчёты», выгрузка — запись «Выгрузка» с грифом отчёта до выдачи файла, правила периода, страница.
/// </summary>
public sealed class RecognitionReportTests : BunitContext, IAsyncLifetime
{
    private const int Me = 42;
    private static readonly AccessContext Access = new("42", 2, [5, 6, 7]);
    private static readonly DateOnly From = new(2026, 10, 1);
    private static readonly DateOnly To = new(2026, 10, 31);

    private static readonly DivisionNode[] DivisionTree =
    [
        new(1, "7Управление", null, null, true, 0),
        new(5, "Отдел наблюдения", null, 1, true, 0, CaseDirection.Surveillance),
        new(6, "Группа 1", null, 5, true, 0),
        new(7, "Отдел установки", null, 1, false, 0, CaseDirection.Establishment),
    ];

    private readonly IUserRoleStore _roles = Substitute.For<IUserRoleStore>();
    private readonly ISubjectProvider _subject = Substitute.For<ISubjectProvider>();
    private readonly IAccessContextProvider _access = Substitute.For<IAccessContextProvider>();
    private readonly IMediaStatistics _statistics = Substitute.For<IMediaStatistics>();
    private readonly IDivisionAdminStore _divisions = Substitute.For<IDivisionAdminStore>();
    private readonly IUserDirectory _users = Substitute.For<IUserDirectory>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IReportRenderer _excel = Substitute.For<IReportRenderer>();
    private readonly IMediator _mediator = Substitute.For<IMediator>();

    public RecognitionReportTests()
    {
        _subject.GetCurrentUserIdAsync(Arg.Any<CancellationToken>()).Returns((int?)Me);
        _roles.GetRoleAsync(Me, Arg.Any<CancellationToken>()).Returns(InvestigationRole.Head);
        _roles.AnyAdministratorAsync(Arg.Any<CancellationToken>()).Returns(true);
        _access.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(Access);
        _divisions.ListAsync(Arg.Any<CancellationToken>()).Returns(DivisionTree);
        _users.GetNameAsync(Me, Arg.Any<CancellationToken>()).Returns("Асанов А. А.");
        _statistics.RecognitionAsync(Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<int?>(), Access, Arg.Any<CancellationToken>())
            .Returns(new RecognitionStatistics(
            [
                new RecognitionCounters(5, 10, 2, 40, 5, 6, 2, 9, 3, 2, 1, 3),
                new RecognitionCounters(6, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
            ], MaxClassification: 2));
        _excel.Format.Returns(ReportFormat.Excel);
        _excel.Render(Arg.Any<ReportView>()).Returns(new ReportDocument([1, 2, 3], "отчёт.xlsx", "application/xlsx"));

        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton(_mediator);
        Services.AddSingleton<TimeProvider>(new FixedClock(new DateTimeOffset(2026, 10, 22, 6, 0, 0, TimeSpan.Zero)));
        var profile = Substitute.For<IProfile>();
        profile.DisplayName.Returns("Следствие");
        Services.AddSingleton(profile);
        SetRendererInfo(new RendererInfo("Server", true));
    }

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

    Task IAsyncLifetime.DisposeAsync() => base.DisposeAsync().AsTask();

    [Fact(DisplayName = "Наименования: «Вышестоящее / Подразделение (ОН)», отдел — и через вышестоящее; без отдела — без скобок")]
    public void Names_include_parent_and_direction()
    {
        var names = RecognitionReportBuilder.Names(DivisionTree);

        names[5].ShouldBe("7Управление / Отдел наблюдения (ОН)");
        names[6].ShouldBe("Отдел наблюдения / Группа 1 (ОН)");
        names[7].ShouldBe("7Управление / Отдел установки (ОУ)");
        names[1].ShouldBe("7Управление");
    }

    [Fact(DisplayName = "Период по местному времени: «с» с начала дня, «по» включительно — до начала следующего дня, в UTC")]
    public void Period_is_local_days_in_utc()
    {
        var bishkek = TimeZoneInfo.CreateCustomTimeZone("UTC+6", TimeSpan.FromHours(6), "UTC+6", "UTC+6");

        var (fromUtc, toUtc) = RecognitionReportBuilder.Period(From, To, bishkek);

        fromUtc.ShouldBe(new DateTime(2026, 9, 30, 18, 0, 0, DateTimeKind.Utc));
        toUtc.ShouldBe(new DateTime(2026, 10, 31, 18, 0, 0, DateTimeKind.Utc));
        fromUtc.Kind.ShouldBe(DateTimeKind.Utc);
    }

    [Fact(DisplayName = "Ячейки: отбраковка числом и долей «5 (12,5 %)», поиски с автосверками в скобках; лиц нет — «0»")]
    public void Cells_and_shares()
    {
        var counters = new RecognitionCounters(5, 10, 2, 40, 5, 6, 2, 9, 3, 2, 1, 3);

        RecognitionReportBuilder.Cells(counters).ShouldBe(["10", "2", "40", "5 (12,5 %)", "6 (2)", "9", "3", "2", "1", "3"]);
        RecognitionReportBuilder.RejectedText(RecognitionCounters.Empty(1)).ShouldBe("0");
        RecognitionReportBuilder.Percent(1, 3).ShouldBe("33,3 %");
        RecognitionReportBuilder.Cells(counters).Count.ShouldBe(RecognitionReportBuilder.Columns.Count - 1);
    }

    [Fact(DisplayName = "Отчёт: строки по наименованию, итог, гриф из счёта, отбор — только разрешённые подразделения; счёт — за период в UTC")]
    public async Task Report_is_built_under_permission()
    {
        var report = (await new RecognitionReportQuery.Handler(Source()).Handle(new RecognitionReportQuery(From, To), CancellationToken.None))
            .Data.ShouldNotBeNull();

        report.Rows.Select(r => r.Division).ShouldBe(["7Управление / Отдел наблюдения (ОН)", "Отдел наблюдения / Группа 1 (ОН)"]);
        report.Total.ImagesProcessed.ShouldBe(11);
        report.Total.FacesRejected.ShouldBe(5);
        report.MaxClassification.ShouldBe((short)2);
        report.Divisions.Select(d => d.Id).ShouldBe([5, 7, 6], ignoreOrder: true);
        report.Divisions.ShouldNotContain(d => d.Id == 1); // вне допуска — не в отборе
        await _statistics.Received(1).RecognitionAsync(
            Arg.Is<DateTime>(d => d.Kind == DateTimeKind.Utc), Arg.Is<DateTime>(d => d.Kind == DateTimeKind.Utc), null, Access, Arg.Any<CancellationToken>());
        new RecognitionReportQuery(From, To).AuditSummary.ShouldBe("investigation:reports:recognition:view");
    }

    [Fact(DisplayName = "Без права «Статистические отчёты» — отказ текстом, счёт не выполняется, файл не выдаётся")]
    public async Task Without_permission_nothing_is_counted()
    {
        _roles.GetRoleAsync(Me, Arg.Any<CancellationToken>()).Returns(InvestigationRole.Investigator);

        (await new RecognitionReportQuery.Handler(Source()).Handle(new RecognitionReportQuery(From, To), CancellationToken.None))
            .StatusMessage.ShouldBe(RecognitionReportSource.Denied);
        (await ExportHandler().Handle(new ExportRecognitionReportCommand(From, To, null, ReportFormat.Excel), CancellationToken.None))
            .StatusMessage.ShouldBe(RecognitionReportSource.Denied);

        await _statistics.DidNotReceiveWithAnyArgs().RecognitionAsync(default, default, default, default!, default);
        await _audit.DidNotReceiveWithAnyArgs().WriteAsync(default!, default);
    }

    [Fact(DisplayName = "Выгрузка: таблица с маркировкой грифа и «Сформировал», запись «Выгрузка» с грифом отчёта и отбором; неизвестный формат — отказ")]
    public async Task Export_marks_and_audits()
    {
        ReportView? rendered = null;
        _excel.Render(Arg.Do<ReportView>(v => rendered = v)).Returns(new ReportDocument([1, 2, 3], "отчёт.xlsx", "application/xlsx"));

        var file = (await ExportHandler().Handle(new ExportRecognitionReportCommand(From, To, 5, ReportFormat.Excel), CancellationToken.None))
            .Data.ShouldNotBeNull();

        file.Content.ShouldBe([1, 2, 3]);
        rendered.ShouldNotBeNull();
        rendered.Title.ShouldBe(RecognitionReportBuilder.Title);
        rendered.ClassificationMarking.ShouldBe(ClassificationLevels.Marking(2));
        rendered.PrintStamp.ShouldStartWith("Сформировал: Асанов А. А.");
        rendered.FilterLines.ShouldBe(["Период: 01.10.2026 — 31.10.2026", "Подразделение: 7Управление / Отдел наблюдения (ОН)"]);
        rendered.Columns.ShouldBe(RecognitionReportBuilder.Columns);
        rendered.Groups.ShouldHaveSingleItem().Rows.Count.ShouldBe(2);
        await _audit.Received(1).WriteAsync(
            Arg.Is<AuditEntry>(e => e.Action == AuditAction.Export && e.Classification == 2 && e.SubjectId == Me
                && e.ObjectRef!.StartsWith("investigation:reports:recognition:export:Excel", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());

        (await ExportHandler().Handle(new ExportRecognitionReportCommand(From, To, null, ReportFormat.Word), CancellationToken.None))
            .Status.ShouldBeFalse();
    }

    [Fact(DisplayName = "Правила: «с» не позже «по», период не длиннее пяти лет, подразделение — положительный номер, формат — известный")]
    public void Validators()
    {
        var query = new RecognitionReportValidator();
        query.Validate(new RecognitionReportQuery(From, To)).IsValid.ShouldBeTrue();
        query.Validate(new RecognitionReportQuery(To, From)).IsValid.ShouldBeFalse();
        query.Validate(new RecognitionReportQuery(From.AddYears(-6), To)).IsValid.ShouldBeFalse();
        query.Validate(new RecognitionReportQuery(From, To, 0)).IsValid.ShouldBeFalse();

        var export = new ExportRecognitionReportValidator();
        export.Validate(new ExportRecognitionReportCommand(From, To, null, ReportFormat.Pdf)).IsValid.ShouldBeTrue();
        export.Validate(new ExportRecognitionReportCommand(From, To, null, (ReportFormat)99)).IsValid.ShouldBeFalse();
    }

    [Fact(DisplayName = "Страница: по умолчанию текущий месяц, таблица по подразделениям с итогом и гриф; «Excel» — выгрузка файлом")]
    public async Task Page_builds_and_exports()
    {
        var report = (await new RecognitionReportQuery.Handler(Source()).Handle(new RecognitionReportQuery(From, To), CancellationToken.None)).Data!;
        _mediator.Send(Arg.Any<RecognitionReportQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<RecognitionReport>>(ResponseDto<RecognitionReport>.Ok(report)));
        _mediator.Send(Arg.Any<ExportRecognitionReportCommand>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<ReportDocument>>(ResponseDto<ReportDocument>.Ok(new ReportDocument([1], "отчёт.xlsx", "x"))));

        Render<MudBlazor.MudPopoverProvider>(); // выпадающий список подразделений
        var cut = Render<RecognitionReportPage>();

        cut.WaitForAssertion(() => cut.Markup.ShouldContain("7Управление / Отдел наблюдения (ОН)"));
        _mediator.Received(1).Send(Arg.Is<RecognitionReportQuery>(q => q.From == new DateOnly(2026, 10, 1) && q.To == new DateOnly(2026, 10, 22)),
            Arg.Any<CancellationToken>());
        cut.Markup.ShouldContain("5 (12,5 %)");
        cut.Markup.ShouldContain("Итого");
        cut.Markup.ShouldContain("гриф отчёта");

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Excel").Click();
        _mediator.Received(1).Send(Arg.Is<ExportRecognitionReportCommand>(c => c.Format == ReportFormat.Excel && c.From == From && c.To == To),
            Arg.Any<CancellationToken>());
        cut.WaitForAssertion(() => JSInterop.Invocations.Select(i => i.Identifier).ShouldContain("downloadFileFromStream"));
    }

    private RecognitionReportSource Source() =>
        new(_roles, _subject, _access, _statistics, _divisions, new FixedClock(new DateTimeOffset(2026, 10, 22, 6, 0, 0, TimeSpan.Zero)));

    private ExportRecognitionReportCommand.Handler ExportHandler() =>
        new(Source(), [_excel], _subject, _users, _audit, new FixedClock(new DateTimeOffset(2026, 10, 22, 6, 0, 0, TimeSpan.Zero)));

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
