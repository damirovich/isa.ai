using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Admin.Application;
using ISC.AI.Modules.Admin.Application.Features.Audit;
using ISC.AI.Modules.Admin.Domain.Services;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Admin;

/// <summary>
/// Журнал аудита (ТБ-030/032): понятное описание действий, выбор сотрудника по имени, выгрузка .csv — всё под правом
/// «читать журнал»; выгрузка ограничена потолком и защищена от исполнения формул в Excel.
/// </summary>
public sealed class AuditJournalTests
{
    private readonly IAuditReader _reader = Substitute.For<IAuditReader>();
    private readonly IAccessContextProvider _access = Substitute.For<IAccessContextProvider>();
    private readonly IPlatformAdministration _administration = Substitute.For<IPlatformAdministration>();
    private readonly IUserAccountStore _accounts = Substitute.For<IUserAccountStore>();
    private readonly IDivisionCatalog _divisions = Substitute.For<IDivisionCatalog>();

    public AuditJournalTests()
    {
        _administration.CanViewAuditAsync(Arg.Any<CancellationToken>()).Returns(true);
        _access.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(new AccessContext("42", 2, [5]));
        _accounts.ListAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new UserAccountRow(7, "ashyrov", "Ашыров Б. Д.", IsActive: true, HasLocalPassword: true, MustChangePassword: false),
            new UserAccountRow(8, "sadykov", null, IsActive: false, HasLocalPassword: true, MustChangePassword: false),
        ]);
        _divisions.ListAsync(Arg.Any<CancellationToken>()).Returns([new DivisionCatalogItem(5, "7Управление", true)]);
    }

    [Theory(DisplayName = "Описание действия: сводка сценария переводится словами, номера — «№», неизвестное остаётся как есть")]
    [InlineData("investigation:case:4:purge", null, "Следствие · дело · № 4 · уничтожение")]
    [InlineData("admin:role:5:Verifier", null, "Администрирование · роль · № 5 · Verifier")]
    [InlineData(null, "doc:17", "Doc · № 17")]
    [InlineData(null, null, "Просмотр")]
    public void Describe_translates_summary(string? summary, string? objectRef, string expected)
    {
        AuditText.Describe(AuditAction.View, summary, objectRef).ShouldBe(expected);
    }

    [Fact(DisplayName = "Строка экрана: имя входа сотрудника и наименование подразделения; неизвестное подразделение — номером")]
    public async Task Rows_are_enriched_with_names()
    {
        _reader.QueryAsync(Arg.Any<AuditFilter>(), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>()).Returns(new AuditPage(
        [
            Row(1, subjectId: 7, divisionId: 5, payload: "investigation:case:4:view"),
            Row(2, subjectId: 99, divisionId: 12),
        ], 2));

        var page = (await new ListAuditRecordsQuery.Handler(_reader, _access, _administration, _accounts, _divisions)
            .Handle(new ListAuditRecordsQuery(new AuditFilter()), CancellationToken.None)).Data!;

        page.Rows[0].SubjectLogin.ShouldBe("ashyrov");
        page.Rows[0].DivisionName.ShouldBe("7Управление");
        page.Rows[0].What.ShouldBe("Следствие · дело · № 4 · просмотр");
        page.Rows[1].SubjectLogin.ShouldBeNull();
        page.Rows[1].DivisionName.ShouldBe("№ 12");
    }

    [Fact(DisplayName = "Без права на журнал: ни записей, ни списка сотрудников, ни выгрузки — порт журнала не спрашивается")]
    public async Task Everything_requires_audit_right()
    {
        _administration.CanViewAuditAsync(Arg.Any<CancellationToken>()).Returns(false);

        (await new ListAuditRecordsQuery.Handler(_reader, _access, _administration, _accounts, _divisions)
            .Handle(new ListAuditRecordsQuery(new AuditFilter()), CancellationToken.None)).StatusMessage.ShouldBe(AdminGuard.AuditDenied);
        (await new ListAuditSubjectsQuery.Handler(_accounts, _administration)
            .Handle(new ListAuditSubjectsQuery(), CancellationToken.None)).StatusMessage.ShouldBe(AdminGuard.AuditDenied);
        (await ExportHandler().Handle(new ExportAuditJournalQuery(new AuditFilter()), CancellationToken.None))
            .StatusMessage.ShouldBe(AdminGuard.AuditDenied);

        await _reader.DidNotReceiveWithAnyArgs().QueryAsync(default!, default!, default);
        await _accounts.DidNotReceiveWithAnyArgs().ListAsync(default);
    }

    [Fact(DisplayName = "Сотрудники для отбора «Кто»: по имени, отключённые тоже; без ФИО — имя входа")]
    public async Task Subjects_are_listed_by_name()
    {
        var subjects = (await new ListAuditSubjectsQuery.Handler(_accounts, _administration)
            .Handle(new ListAuditSubjectsQuery(), CancellationToken.None)).Data!;

        subjects.Select(s => s.Name).ShouldBe(["sadykov", "Ашыров Б. Д."]);
        subjects.Single(s => s.UserId == 8).IsActive.ShouldBeFalse();
    }

    [Fact(DisplayName = "Выгрузка: читает журнал страницами по тому же отбору, UTF-8 с меткой, разделитель «;», сама пишется в журнал как выгрузка")]
    public async Task Export_builds_csv_over_pages()
    {
        var firstPage = Enumerable.Range(1, ExportAuditJournalQuery.ReadPageSize).Select(i => Row(i, 7, 5, "admin:role:5:Verifier")).ToList();
        _reader.QueryAsync(Arg.Is<AuditFilter>(f => f.Page == 1), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>())
            .Returns(new AuditPage(firstPage, 201));
        _reader.QueryAsync(Arg.Is<AuditFilter>(f => f.Page == 2), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>())
            .Returns(new AuditPage([Row(999, 7, 5, "=HYPERLINK(\"x\")")], 201));

        var filter = new AuditFilter(SubjectId: 7, Action: AuditAction.Modify);
        var file = (await ExportHandler().Handle(new ExportAuditJournalQuery(filter), CancellationToken.None)).Data!;

        file.RowCount.ShouldBe(201);
        file.Truncated.ShouldBeFalse();
        file.FileName.ShouldEndWith(".csv");
        file.Content.Take(3).ShouldBe(Encoding.UTF8.GetPreamble());

        var lines = Encoding.UTF8.GetString(file.Content).TrimStart('﻿').Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        lines[0].ShouldBe(AuditJournal.CsvHeader);
        lines.Length.ShouldBe(202);

        // CSV-инъекция: сводка «=HYPERLINK(...)» не станет формулой в Excel.
        lines[^1].ShouldContain("'=HYPERLINK");

        await _reader.Received().QueryAsync(
            Arg.Is<AuditFilter>(f => f.SubjectId == 7 && f.Action == AuditAction.Modify), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>());
        new ExportAuditJournalQuery(filter).AuditAction.ShouldBe(AuditAction.Export);
    }

    [Fact(DisplayName = "Выгрузка упирается в потолок и честно помечает файл неполным")]
    public async Task Export_is_capped()
    {
        _reader.QueryAsync(Arg.Any<AuditFilter>(), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>())
            .Returns(call => new AuditPage(
                [.. Enumerable.Range(0, ExportAuditJournalQuery.ReadPageSize).Select(i => Row(i, 7, 5, "x"))],
                AuditJournal.MaxExportRows + 500));

        var file = (await ExportHandler().Handle(new ExportAuditJournalQuery(new AuditFilter()), CancellationToken.None)).Data!;

        file.RowCount.ShouldBe(AuditJournal.MaxExportRows);
        file.Truncated.ShouldBeTrue();
    }

    [Theory(DisplayName = "Ячейки CSV: кавычки и разделители экранируются, формулы обезвреживаются")]
    [InlineData("простой текст", "простой текст")]
    [InlineData("a;b", "\"a;b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("+7 999", "'+7 999")]
    [InlineData("-1", "'-1")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    public void Csv_cells_are_escaped(string payload, string expectedCell)
    {
        var names = new AuditNames(new Dictionary<int, string>(), new Dictionary<int, string>());
        var line = AuditJournal.ToCsvLine(names.ToJournalRow(Row(1, null, null, payload)));

        line.Split(';').ShouldContain(expectedCell.Contains(';') ? expectedCell.Split(';')[0] : expectedCell);
    }

    private ExportAuditJournalQuery.Handler ExportHandler() =>
        new(_reader, _access, _administration, _accounts, _divisions, TimeProvider.System);

    private static AuditRecordRow Row(long id, int? subjectId, int? divisionId, string? payload = null) =>
        new(id, new DateTime(2026, 9, 30, 8, 0, 0, DateTimeKind.Utc), subjectId, subjectId is null ? null : "Сотрудник",
            AuditAction.Modify, null, 0, divisionId, payload);
}
