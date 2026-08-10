using System.Globalization;
using ClosedXML.Excel;
using ISC.AI.Modules.DocFlow.Domain.Services;

namespace ISC.AI.Modules.DocFlow.Data.Reports;

/// <summary>
/// Отчёт в Excel (ClosedXML, MIT). Один лист: шапка с грифом, штампом печати и применённым отбором,
/// затем группы со строками и итогами.
/// </summary>
/// <remarks>
/// Excel — рабочий формат: его открывают, чтобы отсортировать и посчитать. Поэтому шапка таблицы
/// закреплена, включён автофильтр, а числовые итоги вынесены отдельным блоком, а не подмешаны
/// в строки данных — иначе фильтрация ломала бы суммы.
/// </remarks>
public sealed class ExcelReportRenderer : IReportRenderer
{
    private const double MaxColumnWidth = 60;

    /// <inheritdoc />
    public ReportFormat Format => ReportFormat.Excel;

    /// <inheritdoc />
    public ReportDocument Render(ReportView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        using var workbook = new XLWorkbook();

        // Имя листа ограничено 31 символом — длинный заголовок отчёта туда не влезает.
        var sheet = workbook.Worksheets.Add("Отчёт");
        var row = 1;

        // Гриф — ПЕРВОЙ строкой и заметно (ТБ-033): читающий обязан видеть режим документа сразу,
        // а не после прокрутки.
        sheet.Cell(row, 1).Value = view.ClassificationMarking;
        sheet.Cell(row, 1).Style.Font.Bold = true;
        sheet.Cell(row, 1).Style.Font.FontColor = XLColor.DarkRed;
        row += 2;

        sheet.Cell(row++, 1).Value = view.Title;
        sheet.Cell(row - 1, 1).Style.Font.Bold = true;
        sheet.Cell(row - 1, 1).Style.Font.FontSize = 14;

        foreach (var line in view.FilterLines)
        {
            sheet.Cell(row++, 1).Value = line;
        }

        sheet.Cell(row++, 1).Value = view.PrintStamp;
        row++;

        foreach (var group in view.Groups)
        {
            sheet.Cell(row, 1).Value = group.Title;
            sheet.Cell(row, 1).Style.Font.Bold = true;
            sheet.Cell(row, 1).Style.Fill.BackgroundColor = XLColor.LightGray;
            row++;

            var headerRow = row;
            for (var column = 0; column < view.Columns.Count; column++)
            {
                sheet.Cell(row, column + 1).Value = view.Columns[column];
                sheet.Cell(row, column + 1).Style.Font.Bold = true;
            }

            row++;

            foreach (var dataRow in group.Rows)
            {
                for (var column = 0; column < dataRow.Cells.Count; column++)
                {
                    // Значение пишется КАК ТЕКСТ: рег. номера вида «01-05/123» Excel иначе принимает
                    // за дату и молча их портит — правится это только вручную и уже после печати.
                    sheet.Cell(row, column + 1).SetValue(dataRow.Cells[column]);
                }

                row++;
            }

            // Автофильтр — по шапке своей группы: он и делает выгрузку рабочей, а не «картинкой».
            sheet.Range(headerRow, 1, row - 1, view.Columns.Count).SetAutoFilter();

            foreach (var total in group.Totals)
            {
                sheet.Cell(row, 1).Value = total.Label;
                sheet.Cell(row, 2).SetValue(total.Value);
                row++;
            }

            row++;
        }

        sheet.Cell(row, 1).Value = "ИТОГО";
        sheet.Cell(row, 1).Style.Font.Bold = true;
        row++;

        foreach (var total in view.Totals)
        {
            sheet.Cell(row, 1).Value = total.Label;
            sheet.Cell(row, 2).SetValue(total.Value);
            row++;
        }

        // Ширина по содержимому, но с потолком: краткое содержание документа бывает в тысячу знаков,
        // и без ограничения одна колонка растянула бы лист так, что читать станет нечего.
        sheet.Columns().AdjustToContents();
        foreach (var column in sheet.ColumnsUsed())
        {
            if (column.Width > MaxColumnWidth)
            {
                column.Width = MaxColumnWidth;
            }
        }

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);

        return new ReportDocument(
            stream.ToArray(),
            ReportFileNames.For(view.Title, "xlsx"),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
    }
}

/// <summary>Имя файла отчёта — общее правило для всех форматов.</summary>
internal static class ReportFileNames
{
    /// <summary>
    /// Заголовок + дата формирования. Дата в имени нужна, чтобы выгрузки за разные периоды не
    /// затирали друг друга в папке загрузок; недопустимые в имени символы заменяются.
    /// </summary>
    public static string For(string title, string extension)
    {
        var safe = string.Join('_', title.Split(Path.GetInvalidFileNameChars(),
            StringSplitOptions.RemoveEmptyEntries));

        return $"{safe}_{DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}.{extension}";
    }
}
