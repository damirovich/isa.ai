using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Application.Features.Cases;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using ISC.AI.Profile.Investigation.UI;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Investigation.UI;

/// <summary>
/// Реестр дел (ТФ-ДЕЛ-01) без страницы: состояние списка в адресе (отборы, страница, размер, порядок), подписи строк без
/// повтора номера задания, файл для Excel (гриф, защита от формул, номер — текстом) и выгрузка — запись «Выгрузка» в
/// журнале без текста поиска (ТБ-032), только под определённым сотрудником.
/// </summary>
public sealed class CasesRegistryTests
{
    [Fact(DisplayName = "Состояние реестра: умолчания в адрес не пишутся — /cases; всё заданное читается обратно тем же")]
    public void List_state_round_trips_through_address()
    {
        new CasesListState().ToUri().ShouldBe("/cases");

        var state = new CasesListState("12/24 Иванов", CaseKind.ObjectTask, CaseStatus.Suspended, 7, CaseDirection.Establishment,
            Mine: true, Page: 3, PageSize: 25, Sort: CaseSortField.OpenedAt, SortDescending: true);
        var uri = state.ToUri();

        uri.ShouldStartWith("/cases?q=12%2F24%20");
        uri.ShouldContain("direction=ou");
        CasesListState.Parse(Query(uri)).ShouldBe(state);
    }

    [Fact(DisplayName = "Состояние реестра: подставленный вручную мусор в адресе — умолчание, а не ошибка (размер не из 10/25/50, страница 0, неизвестный статус)")]
    public void List_state_ignores_garbage_in_address()
    {
        var state = CasesListState.Parse(new Dictionary<string, string?>
        {
            ["status"] = "Deleted",
            ["kind"] = "99",
            ["division"] = "-5",
            ["page"] = "0",
            ["size"] = "1000",
            ["sort"] = "Password",
            ["mine"] = "yes",
            ["q"] = "   ",
        });

        state.ShouldBe(new CasesListState());
        CasesListState.Parse(new Dictionary<string, string?> { ["sort"] = "openedat", ["status"] = "closed" })
            .ShouldBe(new CasesListState(Status: CaseStatus.Closed, Sort: CaseSortField.OpenedAt));
    }

    [Fact(DisplayName = "Строка реестра: «задание №» под номером — только если он отличается от номера дела; «Задание № … — » из названия убирается")]
    public void Row_labels_do_not_repeat_task_number()
    {
        var imported = Row("14/24", "Задание № 14/24 — Учебный Марат", taskNumber: "14/24");
        CaseListLabels.ShowsTaskNumber(imported).ShouldBeFalse();
        CaseListLabels.DisplayTitle(imported).ShouldBe("Учебный Марат");

        var other = Row("УД-5", "Задание № 3/24 — Проверка адреса", taskNumber: "3/24");
        CaseListLabels.ShowsTaskNumber(other).ShouldBeTrue();
        CaseListLabels.DisplayTitle(other).ShouldBe("Проверка адреса");

        // Название — только номер: показывать нечего, кроме него самого.
        CaseListLabels.DisplayTitle(Row("7", "Задание № 7", taskNumber: "7")).ShouldBe("Задание № 7");
        CaseListLabels.DisplayTitle(Row("УД-1", "Кража")).ShouldBe("Кража");
    }

    [Fact(DisplayName = "Файл для Excel: BOM и «;», гриф файла — наибольший из выгруженных дел (первой и последней строкой), номер — текстом, формула в названии — как текст")]
    public void Csv_marks_classification_and_neutralizes_formulas()
    {
        var rows = new[]
        {
            Row("12/24", "=HYPERLINK(\"http://x\")", classification: ClassificationLevels.ForOfficialUse, taskNumber: "12/24"),
            Row("УД-2", "Кража; со \"взломом\"", classification: ClassificationLevels.Secret, investigator: 42),
        };
        var names = new CasesCsv.Names(id => "Подразделение " + id, _ => "ГУ-1", id => id == 42 ? "Петров П.П." : "—");

        var bytes = CasesCsv.Build(rows, total: 2, names, "Иванов И.И.", new DateTime(2026, 10, 14, 9, 5, 0));

        bytes.Take(3).ShouldBe(Encoding.UTF8.GetPreamble());
        var lines = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3).Split("\r\n");
        lines[0].ShouldBe("СЕКРЕТНО");
        lines[1].ShouldStartWith("Номер;Задание №;Отдел;Название;");
        lines[2].ShouldStartWith("\"=\"\"12/24\"\"\";\"=\"\"12/24\"\"\";;\"'=HYPERLINK(\"\"http://x\"\")\";");
        lines[3].ShouldContain(";\"Кража; со \"\"взломом\"\"\";");
        lines[3].ShouldContain(";Петров П.П.;ГУ-1;Подразделение 3;Секретно;");
        lines[5].ShouldBe("Сформировал: Иванов И.И. · 14.10.2026 09:05 · дел: 2");
        lines[6].ShouldBe("СЕКРЕТНО");
    }

    [Fact(DisplayName = "Файл для Excel: урезанная пределом выгрузка подписана «выгружено N из M»; пустая — «НЕСЕКРЕТНО»")]
    public void Csv_reports_truncation_and_empty_marking()
    {
        var names = new CasesCsv.Names(_ => "П", _ => "", _ => "");
        var text = Encoding.UTF8.GetString(CasesCsv.Build([Row("1", "А")], total: 9000, names, "И", DateTime.Today));
        text.ShouldContain("выгружено 1 из 9000 (предел выгрузки)");

        Encoding.UTF8.GetString(CasesCsv.Build([], 0, names, "И", DateTime.Today)).ShouldContain("НЕСЕКРЕТНО");
        CasesCsv.Escape("-1+2").ShouldBe("'-1+2");
        CasesCsv.Escape("@SUM(A1)").ShouldBe("'@SUM(A1)");
        CasesCsv.Escape("обычный").ShouldBe("обычный");
    }

    [Fact(DisplayName = "Выгрузка реестра: запись «Выгрузка» без текста поиска (ТБ-032); те же отборы и порядок, до 5000 строк; подпись — имя сотрудника; без сотрудника — отказ")]
    public async Task Export_is_audited_and_uses_list_filters()
    {
        var query = new ExportCasesQuery("Иванов", CaseKind.ObjectTask, CaseStatus.InProgress, 5, 42, CaseDirection.Surveillance,
            CaseSortField.OpenedAt, SortDescending: true);
        query.AuditAction.ShouldBe(AuditAction.Export);
        query.AuditSummary.ShouldBe(
            "investigation:cases:export:text=*;kind=ObjectTask;status=InProgress;division=5;investigator=42;direction=Surveillance;format=csv");
        query.AuditSummary.ShouldNotContain("Иванов");

        var store = Substitute.For<ICaseStore>();
        var accessProvider = Substitute.For<IAccessContextProvider>();
        var subject = Substitute.For<ISubjectProvider>();
        var accounts = Substitute.For<IUserAccountStore>();
        var access = new AccessContext("42", 2, [5]);
        accessProvider.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(access);
        accounts.ListAsync(Arg.Any<CancellationToken>()).Returns(
            [new UserAccountRow(42, "petrov", "Петров П.П.", IsActive: true, HasLocalPassword: true, MustChangePassword: false)]);
        CaseFilter? seen = null;
        store.ListAsync(Arg.Do<CaseFilter>(f => seen = f), access, Arg.Any<CancellationToken>())
            .Returns(new CasePage([Row("1", "А")], 7000));
        var handler = new ExportCasesQuery.Handler(store, accessProvider, subject, accounts);

        subject.GetCurrentUserIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        (await handler.Handle(query, CancellationToken.None)).StatusMessage.ShouldBe(ExportCasesQuery.NoSubject);
        await store.DidNotReceiveWithAnyArgs().ListAsync(default!, default!, default);

        subject.GetCurrentUserIdAsync(Arg.Any<CancellationToken>()).Returns((int?)42);
        var export = (await handler.Handle(query, CancellationToken.None)).Data.ShouldNotBeNull();

        export.Author.ShouldBe("Петров П.П.");
        export.Truncated.ShouldBeTrue();
        seen.ShouldBe(new CaseFilter("Иванов", CaseKind.ObjectTask, CaseStatus.InProgress, 5, 42, 1, ExportCasesQuery.MaxRows,
            CaseDirection.Surveillance, CaseSortField.OpenedAt, SortDescending: true));
    }

    private static Dictionary<string, string?> Query(string uri) =>
        uri[(uri.IndexOf('?', StringComparison.Ordinal) + 1)..].Split('&')
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => p[0], p => (string?)Uri.UnescapeDataString(p[1]));

    private static CaseRow Row(string number, string title, short classification = 0, string? taskNumber = null, int? investigator = null) =>
        new(1, number, title, CaseKind.ObjectTask, CaseStatus.InProgress, new DateOnly(2026, 10, 1), investigator, DivisionId: 3,
            classification, MediaCount: 2, PersonCount: 1, TaskNumber: taskNumber);
}
