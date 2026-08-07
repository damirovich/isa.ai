namespace ISC.AI.Modules.DocFlow.Domain.Services;

/// <summary>Готовый файл отчёта: содержимое, имя и MIME-тип для выдачи пользователю.</summary>
public sealed record ReportDocument(byte[] Content, string FileName, string ContentType);

/// <summary>
/// Отрисовка отчёта в конкретный формат (разд. 6 ТЗ СКИД: PDF, Excel, Word).
/// </summary>
/// <remarks>
/// Реализации получают УЖЕ СОБРАННЫЙ <see cref="ReportView"/> и не знают ни про базу, ни про допуск:
/// отбор строк и решётка отработали раньше (<see cref="IReportDataSource"/>). Рендерер не вправе
/// опустить маркировку грифа и штамп печати — они часть режимных требований (ТБ-033, п. 8.3 ТЗ),
/// а не оформления.
/// </remarks>
public interface IReportRenderer
{
    /// <summary>Формат, который умеет этот рендерер.</summary>
    ReportFormat Format { get; }

    /// <summary>Рисует отчёт.</summary>
    ReportDocument Render(ReportView view);
}
