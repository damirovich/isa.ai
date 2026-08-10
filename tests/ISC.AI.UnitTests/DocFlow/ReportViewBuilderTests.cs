using System;
using System.Collections.Generic;
using System.Linq;
using ISC.AI.Modules.DocFlow.Domain.Enums;
using ISC.AI.Modules.DocFlow.Domain.Services;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.DocFlow;

/// <summary>
/// Сборка отчёта (разд. 6 ТЗ СКИД): группировка по виду, итоги и маркировка грифа.
/// Чистая доменная логика — без БД и без файлов.
/// </summary>
public sealed class ReportViewBuilderTests
{
    private static readonly ReportFilter Period = new(new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31));

    private static readonly ReportNames Names = new(
        new Dictionary<int, string> { [10] = "ГИ", [20] = "Аппарат" },
        new Dictionary<int, string> { [1] = "Иванов И.", [2] = "Петров П.", [3] = "Сидоров С." });

    [Fact(DisplayName = "Отчёт по ответственным группируется по исполнителю")]
    public void ByAssignee_groups_by_assignee()
    {
        var data = new ReportData(
        [
            Row(assignmentId: 1, assignee: 1),
            Row(assignmentId: 2, assignee: 2),
            Row(assignmentId: 3, assignee: 1),
        ], MaxClassification: 0);

        var view = ReportViewBuilder.Build(ReportKind.ByAssignee, Period, data, Names, "Тест", DateTime.UtcNow);

        view.Groups.Select(group => group.Title).ShouldBe(["Иванов И.", "Петров П."]);
        view.Groups.Single(group => group.Title == "Иванов И.").Rows.Count.ShouldBe(2);
    }

    [Fact(DisplayName = "Отчёт по срокам не дробится на группы — это единый список")]
    public void ByDeadline_keeps_single_group()
    {
        var data = new ReportData(
            [Row(assignmentId: 1, assignee: 1), Row(assignmentId: 2, assignee: 2, divisionId: 20)],
            MaxClassification: 0);

        var view = ReportViewBuilder.Build(ReportKind.ByDeadline, Period, data, Names, "Тест", DateTime.UtcNow);

        view.Groups.Count.ShouldBe(1);
        view.Groups[0].Rows.Count.ShouldBe(2);
    }

    [Fact(DisplayName = "Просроченные группируются по подразделению")]
    public void Overdue_groups_by_division()
    {
        var data = new ReportData(
        [
            Row(assignmentId: 1, assignee: 1, divisionId: 10, status: AssignmentStatus.Overdue),
            Row(assignmentId: 2, assignee: 2, divisionId: 20, status: AssignmentStatus.Overdue),
        ], MaxClassification: 0);

        var view = ReportViewBuilder.Build(ReportKind.Overdue, Period, data, Names, "Тест", DateTime.UtcNow);

        view.Groups.Select(group => group.Title).ShouldBe(["Аппарат", "ГИ"]);
    }

    /// <summary>
    /// Гранула строки — НАЗНАЧЕНИЕ: документ с двумя поручениями даёт две строки, но один документ.
    /// Сложить их в одно число значило бы завысить объём работы вдвое.
    /// </summary>
    [Fact(DisplayName = "Строки и документы считаются раздельно")]
    public void Totals_count_rows_and_documents_separately()
    {
        var data = new ReportData(
        [
            Row(assignmentId: 1, assignee: 1, documentId: 100),
            Row(assignmentId: 2, assignee: 2, documentId: 100),
            Row(assignmentId: 3, assignee: 3, documentId: 200, status: AssignmentStatus.Done),
        ], MaxClassification: 0);

        var view = ReportViewBuilder.Build(ReportKind.ByAssignee, Period, data, Names, "Тест", DateTime.UtcNow);

        Total(view.Totals, "Строк (поручений)").ShouldBe("3");
        Total(view.Totals, "Документов").ShouldBe("2");
        Total(view.Totals, "Исполнено").ShouldBe("1");
    }

    /// <summary>
    /// ИНВАРИАНТ ТБ-033: сводка по документам ДСП сама является ДСП — маркировка обязана попасть
    /// в файл, и она берётся по МАКСИМАЛЬНОМУ грифу выданных строк, а не по среднему или первому.
    /// </summary>
    [Theory(DisplayName = "Маркировка грифа: 0 — «Открыто», иначе ДСП с номером")]
    [InlineData((short)0, "Открыто")]
    [InlineData((short)2, "ДСП. Гриф: 2")]
    public void Marking_follows_max_classification(short classification, string expected)
    {
        var data = new ReportData([Row(assignmentId: 1, assignee: 1)], classification);

        var view = ReportViewBuilder.Build(ReportKind.ByAssignee, Period, data, Names, "Тест", DateTime.UtcNow);

        view.ClassificationMarking.ShouldBe(expected);
    }

    [Fact(DisplayName = "Применённый отбор печатается в самом файле")]
    public void Filter_lines_describe_applied_filter()
    {
        var filter = Period with { DivisionId = 10, Status = AssignmentStatus.InProgress };
        var data = new ReportData([Row(assignmentId: 1, assignee: 1)], MaxClassification: 0);

        var view = ReportViewBuilder.Build(ReportKind.ByDivision, filter, data, Names, "Тест", DateTime.UtcNow);

        view.FilterLines.ShouldContain("Период: 01.01.2026 — 31.12.2026");
        view.FilterLines.ShouldContain("Подразделение: ГИ");
        view.FilterLines.ShouldContain($"Статус: {AssignmentStatusNames.Of(AssignmentStatus.InProgress)}");
    }

    [Fact(DisplayName = "Штамп печати содержит автора выгрузки")]
    public void Print_stamp_names_the_author()
    {
        var data = new ReportData([Row(assignmentId: 1, assignee: 1)], MaxClassification: 0);

        var view = ReportViewBuilder.Build(
            ReportKind.ByAssignee, Period, data, Names, "Иванов И.", DateTime.UtcNow);

        view.PrintStamp.ShouldContain("Иванов И.");
    }

    /// <summary>Неизвестный человек не должен исчезать из отчёта — он становится номером, а не пустотой.</summary>
    [Fact(DisplayName = "Отсутствующее значение печатается прочерком, неизвестный пользователь — номером")]
    public void Missing_values_are_rendered_explicitly()
    {
        // Срок обнуляется через `with`: у хелпера null означает «взять значение по умолчанию».
        var row = Row(assignmentId: 1, assignee: null, inspector: 99, regNumber: null) with { Deadline = null };
        var data = new ReportData([row], MaxClassification: 0);

        var cells = ReportViewBuilder
            .Build(ReportKind.ByDeadline, Period, data, Names, "Тест", DateTime.UtcNow)
            .Groups[0].Rows[0].Cells;

        cells[0].ShouldBe(ReportViewBuilder.Empty);          // рег. номер
        cells[5].ShouldBe(ReportViewBuilder.Empty);          // исполнитель
        cells[6].ShouldBe("пользователь №99");               // инспектор вне справочника
        cells[7].ShouldBe(ReportViewBuilder.Empty);          // срок
    }

    private static string Total(IReadOnlyList<ReportTotal> totals, string label) =>
        totals.Single(total => total.Label == label).Value;

    private static ReportRow Row(
        int assignmentId,
        int? assignee,
        int documentId = 1,
        int divisionId = 10,
        int? inspector = 3,
        string? regNumber = "01-05/1",
        DateOnly? deadline = null,
        AssignmentStatus status = AssignmentStatus.InProgress) =>
        new(documentId, assignmentId, regNumber, new DateOnly(2026, 3, 1), "Письмо", "Содержание",
            divisionId, assignee, inspector, deadline ?? new DateOnly(2026, 4, 1), status, 0);
}
