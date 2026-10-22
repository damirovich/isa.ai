using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Domain.Services;
using ISC.AI.Modules.Media.Domain.Services;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;

namespace ISC.AI.Profile.Investigation.Application.Features.Statistics;

/// <summary>Подразделение для отбора отчёта (только разрешённые субъекту).</summary>
/// <param name="Id">Подразделение.</param>
/// <param name="Name">Полное наименование с отделом.</param>
public sealed record ReportDivisionOption(int Id, string Name);

/// <summary>Строка отчёта результативности: подразделение и его счётчики.</summary>
/// <param name="Division">Полное наименование подразделения с отделом (ОН/ОУ/ОТМ).</param>
/// <param name="Counters">Счётчики.</param>
public sealed record RecognitionReportRow(string Division, RecognitionCounters Counters);

/// <summary>
/// Сводный отчёт результативности биометрической идентификации (ТФ-ОТЧ-07, [АИС 6.5], ADR-0061).
/// </summary>
/// <param name="From">Начало периода (включительно).</param>
/// <param name="To">Конец периода (включительно).</param>
/// <param name="DivisionId">Отбор по подразделению; <see langword="null"/> — все разрешённые.</param>
/// <param name="Rows">Строки по подразделениям, по наименованию.</param>
/// <param name="Total">Итог по всем строкам.</param>
/// <param name="MaxClassification">Гриф отчёта — наибольший гриф записей, вошедших в счёт (ТБ-033).</param>
/// <param name="Divisions">Подразделения для отбора — разрешённые субъекту.</param>
public sealed record RecognitionReport(
    DateOnly From,
    DateOnly To,
    int? DivisionId,
    IReadOnlyList<RecognitionReportRow> Rows,
    RecognitionCounters Total,
    short MaxClassification,
    IReadOnlyList<ReportDivisionOption> Divisions);

/// <summary>
/// Сборка отчёта результативности (ADR-0061): период → UTC, наименования подразделений, строки и итог, таблица для
/// выгрузки тем же представлением, что отчёты документооборота (<see cref="ReportView"/>).
/// </summary>
public static class RecognitionReportBuilder
{
    /// <summary>Заголовок отчёта (он же основа имени файла).</summary>
    public const string Title = "Результативность распознавания лиц";

    /// <summary>Колонки таблицы — в порядке ячеек <see cref="Cells"/>.</summary>
    public static IReadOnlyList<string> Columns { get; } =
    [
        "Подразделение",
        "Обработано фото",
        "Обработано видео",
        "Найдено лиц",
        "Отбраковано по качеству",
        "Поисков (из них автосверок)",
        "Совпадений выше порога",
        "Подтверждено",
        "Отклонено",
        "Не определено",
        "На верификации",
    ];

    /// <summary>
    /// Период дат по местному времени сервера → полуинтервал UTC [начало «с»; начало дня после «по»).
    /// </summary>
    public static (DateTime FromUtc, DateTime ToUtc) Period(DateOnly from, DateOnly to, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return (
            TimeZoneInfo.ConvertTimeToUtc(from.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), zone),
            TimeZoneInfo.ConvertTimeToUtc(to.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified), zone));
    }

    /// <summary>
    /// Наименования подразделений для отчёта: «Вышестоящее / Подразделение (ОН)» — однофамильные отделы разных
    /// управлений различимы, отдел виден сразу (ADR-0039).
    /// </summary>
    public static IReadOnlyDictionary<int, string> Names(IReadOnlyList<DivisionNode> divisions)
    {
        ArgumentNullException.ThrowIfNull(divisions);
        var byId = divisions.ToDictionary(d => d.Id);
        var directions = DivisionDirections.Resolve(divisions);
        return divisions.ToDictionary(
            d => d.Id,
            d =>
            {
                var name = d.ParentId is { } parentId && byId.TryGetValue(parentId, out var parent) ? parent.Name + " / " + d.Name : d.Name;
                return directions.TryGetValue(d.Id, out var direction) ? $"{name} ({direction.ShortLabel()})" : name;
            });
    }

    /// <summary>Отчёт: строки по наименованию, итог, отбор подразделений — только разрешённые субъекту.</summary>
    public static RecognitionReport Build(
        RecognitionStatistics statistics,
        IReadOnlyDictionary<int, string> names,
        AccessContext access,
        DateOnly from,
        DateOnly to,
        int? divisionId)
    {
        ArgumentNullException.ThrowIfNull(statistics);
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(access);

        var rows = statistics.Divisions
            .Select(c => new RecognitionReportRow(NameOf(names, c.DivisionId), c))
            .OrderBy(r => r.Division, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var total = rows.Aggregate(RecognitionCounters.Empty(0), (sum, row) => sum.Plus(row.Counters));
        var options = access.AllowedDivisions
            .Distinct()
            .Select(id => new ReportDivisionOption(id, NameOf(names, id)))
            .OrderBy(o => o.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return new RecognitionReport(from, to, divisionId, rows, total, statistics.MaxClassification, options);
    }

    /// <summary>Ячейки строки — в порядке <see cref="Columns"/> (без первой колонки).</summary>
    public static IReadOnlyList<string> Cells(RecognitionCounters c)
    {
        ArgumentNullException.ThrowIfNull(c);
        return
        [
            Number(c.ImagesProcessed),
            Number(c.VideosProcessed),
            Number(c.FacesFound),
            RejectedText(c),
            c.AutoSearches > 0 ? $"{Number(c.Searches)} ({Number(c.AutoSearches)})" : Number(c.Searches),
            Number(c.Matches),
            Number(c.Confirmed),
            Number(c.Rejected),
            Number(c.Undetermined),
            Number(c.Pending),
        ];
    }

    /// <summary>Отбраковка: число и доля от найденных лиц — «3 (12,5 %)»; лиц нет — «0».</summary>
    public static string RejectedText(RecognitionCounters c)
    {
        ArgumentNullException.ThrowIfNull(c);
        return c.FacesFound == 0 ? "0" : $"{Number(c.FacesRejected)} ({Percent(c.FacesRejected, c.FacesFound)})";
    }

    /// <summary>Доля в процентах с одним знаком, запятая — десятичный разделитель: «12,5 %».</summary>
    public static string Percent(int part, int whole) =>
        whole == 0 ? "—" : (part * 100.0 / whole).ToString("0.0", CultureInfo.InvariantCulture).Replace('.', ',') + " %";

    /// <summary>Строки отбора для файла и журнала: период и подразделение.</summary>
    public static IReadOnlyList<string> FilterLines(RecognitionReport report, IReadOnlyDictionary<int, string> names)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(names);
        return
        [
            $"Период: {Date(report.From)} — {Date(report.To)}",
            "Подразделение: " + (report.DivisionId is { } id ? NameOf(names, id) : "все в пределах допуска"),
        ];
    }

    /// <summary>
    /// Представление для выгрузки (Excel, Word, PDF): маркировка грифа, «Сформировал», отбор, таблица и итоги.
    /// </summary>
    public static ReportView ToView(
        RecognitionReport report, IReadOnlyDictionary<int, string> names, string generatedBy, DateTime generatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(report);
        var rows = report.Rows
            .Select(r => new ReportViewRow([r.Division, .. Cells(r.Counters)]))
            .ToList();
        var t = report.Total;
        var totals = new List<ReportTotal>
        {
            new("Обработано фото и видео", Number(t.ImagesProcessed + t.VideosProcessed)),
            new("Найдено лиц", Number(t.FacesFound)),
            new("Отбраковано кадров по качеству", RejectedText(t)),
            new("Совпадений выше порога", Number(t.Matches)),
            new("Подтверждено сотрудниками", Number(t.Confirmed)),
            new("Отклонено сотрудниками", Number(t.Rejected)),
        };
        return new ReportView(
            Title,
            ClassificationLevels.Marking(report.MaxClassification),
            $"Сформировал: {generatedBy}. Дата и время: {generatedAtUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture)}.",
            FilterLines(report, names),
            Columns,
            [new ReportViewGroup("По подразделениям", rows, [new ReportTotal("Итого по строкам", string.Join(" · ", Cells(t)))])],
            totals);
    }

    private static string NameOf(IReadOnlyDictionary<int, string> names, int id) =>
        names.TryGetValue(id, out var name) ? name : "подразделение № " + id.ToString(CultureInfo.InvariantCulture);

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Date(DateOnly date) => date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
}
