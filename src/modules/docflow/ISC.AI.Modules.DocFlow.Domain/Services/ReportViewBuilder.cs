using System.Globalization;
using ISC.AI.Modules.DocFlow.Domain.Enums;

namespace ISC.AI.Modules.DocFlow.Domain.Services;

/// <summary>
/// Сборка отчёта из строк выборки (разд. 6 ТЗ СКИД): группировка, итоги и форматирование значений.
/// Чистая доменная логика без ввода-вывода — её проверяют юнит-тестами, без БД и без файлов.
/// </summary>
public static class ReportViewBuilder
{
    /// <summary>Пустое значение печатается длинным тире, а не пустотой (перенос решения СКИД).</summary>
    public const string Empty = "—";

    private static readonly string[] ColumnTitles =
    [
        "Рег. номер", "Дата рег.", "Тип", "Краткое содержание",
        "Подразделение", "Исполнитель", "Инспектор", "Срок", "Статус",
    ];

    /// <summary>Человекочитаемое название вида отчёта — идёт в заголовок файла.</summary>
    public static string TitleOf(ReportKind kind) => kind switch
    {
        ReportKind.ByAssignee => "Отчёт по ответственным лицам",
        ReportKind.ByInspector => "Отчёт по инспекторам",
        ReportKind.ByDivision => "Отчёт по подразделениям",
        ReportKind.ByDeadline => "Отчёт по срокам исполнения",
        ReportKind.Overdue => "Отчёт по просроченным поручениям",
        ReportKind.ByDocumentType => "Отчёт по типам документов",
        _ => "Отчёт по документам",
    };

    /// <summary>Собирает отчёт к выводу.</summary>
    public static ReportView Build(
        ReportKind kind,
        ReportFilter filter,
        ReportData data,
        ReportNames names,
        string generatedBy,
        DateTime generatedAt)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(names);

        var groups = data.Rows
            .GroupBy(row => GroupTitle(kind, row, names))
            .OrderBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(group => new ReportViewGroup(
                group.Key,
                [.. group.Select(row => new ReportViewRow(Cells(row, names)))],
                Totals(group)))
            .ToList();

        return new ReportView(
            TitleOf(kind),
            Marking(data.MaxClassification),
            // Штамп печати п. 8.3: кто и когда. Время — местное для читателя отчёта, не UTC.
            $"Сформировал: {generatedBy}. Дата и время: {generatedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture)}.",
            FilterLines(filter, names),
            ColumnTitles,
            groups,
            Totals(data.Rows));
    }

    // Гриф в тексте документа (ТБ-033). Ноль — «открыто»: писать «гриф 0» бессмысленно.
    private static string Marking(short classification) =>
        classification == 0 ? "Открыто" : $"ДСП. Гриф: {classification.ToString(CultureInfo.InvariantCulture)}";

    private static string GroupTitle(ReportKind kind, ReportRow row, ReportNames names) => kind switch
    {
        ReportKind.ByAssignee => UserName(row.AssigneeUserId, names),
        ReportKind.ByInspector => UserName(row.InspectorUserId, names),
        ReportKind.ByDocumentType => row.TypeName,

        // «Просроченные» группируются по подразделению: разбор просрочек ведут именно по нему.
        ReportKind.ByDivision or ReportKind.Overdue => DivisionName(row.DivisionId, names),

        // «По срокам» — единый список, отсортированный по сроку: дробить его на группы значит
        // разорвать ровно тот порядок, ради которого отчёт и берут.
        _ => "Все поручения",
    };

    private static IReadOnlyList<string> Cells(ReportRow row, ReportNames names) =>
    [
        row.RegNumber ?? Empty,
        row.RegDate.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture),
        row.TypeName,
        row.ShortContent,
        DivisionName(row.DivisionId, names),
        UserName(row.AssigneeUserId, names),
        UserName(row.InspectorUserId, names),
        row.Deadline?.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) ?? Empty,
        AssignmentStatusNames.Of(row.Status),
    ];

    /// <summary>
    /// Итоги. «Документов» считается ОТДЕЛЬНО от «строк»: гранула строки — назначение, и документ
    /// с тремя поручениями даёт три строки; сложить их в одно число значило бы завысить объём работы.
    /// </summary>
    private static IReadOnlyList<ReportTotal> Totals(IEnumerable<ReportRow> rows)
    {
        var list = rows as IReadOnlyList<ReportRow> ?? [.. rows];

        return
        [
            new("Строк (поручений)", list.Count.ToString(CultureInfo.InvariantCulture)),
            new("Документов", list.Select(r => r.DocumentId).Distinct().Count()
                .ToString(CultureInfo.InvariantCulture)),
            new("В работе", Count(list, AssignmentStatus.InProgress, AssignmentStatus.InControl,
                AssignmentStatus.PartiallyDone, AssignmentStatus.Registered)),
            new("Исполнено", Count(list, AssignmentStatus.Done)),
            new("Просрочено", Count(list, AssignmentStatus.Overdue)),
            new("Снято с контроля", Count(list, AssignmentStatus.Closed)),
        ];
    }

    private static string Count(IReadOnlyList<ReportRow> rows, params AssignmentStatus[] statuses) =>
        rows.Count(r => Array.IndexOf(statuses, r.Status) >= 0).ToString(CultureInfo.InvariantCulture);

    // Применённый отбор печатается в самом файле: без него отчёт нельзя ни перепроверить, ни
    // сопоставить с другим — «полный это список или срез» по таблице уже не восстановить.
    private static IReadOnlyList<string> FilterLines(ReportFilter filter, ReportNames names)
    {
        var lines = new List<string>
        {
            $"Период: {filter.From.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)} — "
            + filter.To.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture),
        };

        if (filter.DivisionId is { } divisionId)
        {
            lines.Add($"Подразделение: {DivisionName(divisionId, names)}");
        }

        if (filter.AssigneeUserId is { } assignee)
        {
            lines.Add($"Исполнитель: {UserName(assignee, names)}");
        }

        if (filter.InspectorUserId is { } inspector)
        {
            lines.Add($"Инспектор: {UserName(inspector, names)}");
        }

        if (filter.Status is { } status)
        {
            lines.Add($"Статус: {AssignmentStatusNames.Of(status)}");
        }

        return lines;
    }

    private static string DivisionName(int id, ReportNames names) =>
        names.Divisions.TryGetValue(id, out var name) ? name : $"подразделение №{id.ToString(CultureInfo.InvariantCulture)}";

    private static string UserName(int? id, ReportNames names) =>
        id is { } value
            ? names.Users.TryGetValue(value, out var name) ? name : $"пользователь №{value.ToString(CultureInfo.InvariantCulture)}"
            : Empty;
}
