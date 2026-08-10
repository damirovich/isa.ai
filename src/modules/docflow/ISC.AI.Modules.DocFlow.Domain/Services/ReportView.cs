namespace ISC.AI.Modules.DocFlow.Domain.Services;

// Готовая к выводу модель отчёта (разд. 6 ТЗ СКИД) — общая для Excel/Word/PDF.
// Логика сборки — в ReportViewBuilder.cs (разрез «контракты отдельно от логики», 2026-08-10).

/// <summary>Итоговый показатель отчёта или его группы.</summary>
public sealed record ReportTotal(string Label, string Value);

/// <summary>Строка отчёта, уже подготовленная к выводу: все значения — текст, в порядке колонок.</summary>
public sealed record ReportViewRow(IReadOnlyList<string> Cells);

/// <summary>Группа строк отчёта со своими итогами.</summary>
public sealed record ReportViewGroup(string Title, IReadOnlyList<ReportViewRow> Rows, IReadOnlyList<ReportTotal> Totals);

/// <summary>
/// Готовый к выводу отчёт: заголовок, реквизиты печати, колонки, группы и общие итоги.
/// Модель ОБЩАЯ для всех форматов — Excel, Word и PDF рисуют одно и то же, различаясь лишь способом.
/// </summary>
/// <param name="ClassificationMarking">
/// Маркировка грифа (ТБ-033). Сводка по документам ДСП сама является ДСП, поэтому гриф проставляется
/// в самом файле — как это уже делает экспорт документов в ядре.
/// </param>
/// <param name="PrintStamp">
/// Штамп печати (п. 8.3 ТЗ): кто и когда сформировал. Прослеживаемость печати ДСП — режимное
/// требование, поэтому строка идёт в КАЖДЫЙ формат, а не только в PDF, как было в СКИД.
/// </param>
public sealed record ReportView(
    string Title,
    string ClassificationMarking,
    string PrintStamp,
    IReadOnlyList<string> FilterLines,
    IReadOnlyList<string> Columns,
    IReadOnlyList<ReportViewGroup> Groups,
    IReadOnlyList<ReportTotal> Totals);

/// <summary>Справочные имена для подстановки в отчёт (подразделения и пользователи).</summary>
public sealed record ReportNames(
    IReadOnlyDictionary<int, string> Divisions,
    IReadOnlyDictionary<int, string> Users);
