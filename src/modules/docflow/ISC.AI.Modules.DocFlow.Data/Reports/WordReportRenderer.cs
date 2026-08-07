using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
// Псевдонимы: в GlobalUsings модуля есть Domain.Entities, где свои Document и Table —
// без явного указания имена конфликтуют с одноимёнными типами OpenXml.
using WordDocument = DocumentFormat.OpenXml.Wordprocessing.Document;
using WordTable = DocumentFormat.OpenXml.Wordprocessing.Table;
using ISC.AI.Modules.DocFlow.Domain.Services;

namespace ISC.AI.Modules.DocFlow.Data.Reports;

/// <summary>
/// Отчёт в Word (OpenXml). Альбомная ориентация, шапка таблицы повторяется на каждой странице.
/// </summary>
/// <remarks>
/// Word — формат для подписи и подшивки, поэтому важны две вещи, которых нет у Excel: гриф уходит
/// ещё и в СВОЙСТВА файла (ТБ-033 — маркировка должна пережить копирование текста), а шапка таблицы
/// помечена как повторяемая, иначе со второй страницы читатель видит колонки без названий.
/// </remarks>
public sealed class WordReportRenderer : IReportRenderer
{
    /// <inheritdoc />
    public ReportFormat Format => ReportFormat.Word;

    /// <inheritdoc />
    public ReportDocument Render(ReportView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        using var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            main.Document = new WordDocument();
            var body = main.Document.AppendChild(new Body());

            // Гриф — первой строкой, жирным (ТБ-033).
            body.AppendChild(Text(view.ClassificationMarking, bold: true));
            body.AppendChild(Text(view.Title, bold: true, size: 32));

            foreach (var line in view.FilterLines)
            {
                body.AppendChild(Text(line));
            }

            body.AppendChild(Text(view.PrintStamp));

            foreach (var group in view.Groups)
            {
                body.AppendChild(Text(group.Title, bold: true, size: 26));
                body.AppendChild(Table(view.Columns, group));

                foreach (var total in group.Totals)
                {
                    body.AppendChild(Text($"{total.Label}: {total.Value}"));
                }
            }

            body.AppendChild(Text("ИТОГО", bold: true, size: 26));
            foreach (var total in view.Totals)
            {
                body.AppendChild(Text($"{total.Label}: {total.Value}"));
            }

            // Альбомная ориентация: девять колонок в книжную страницу не помещаются читаемо.
            body.AppendChild(new SectionProperties(
                new PageSize { Width = 16838, Height = 11906, Orient = PageOrientationValues.Landscape }));

            main.Document.Save();

            // Гриф В МЕТАДАННЫХ — чтобы маркировка не терялась при копировании текста из файла.
            document.PackageProperties.Category = view.ClassificationMarking;
            document.PackageProperties.Keywords = view.ClassificationMarking;
            document.PackageProperties.Title = view.Title;
            document.PackageProperties.Description = view.PrintStamp;
        }

        return new ReportDocument(
            stream.ToArray(),
            ReportFileNames.For(view.Title, "docx"),
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document");
    }

    private static Paragraph Text(string value, bool bold = false, int size = 20)
    {
        var properties = new RunProperties(new FontSize { Val = size.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        if (bold)
        {
            properties.AppendChild(new Bold());
        }

        // SpaceProcessingModeValues.Preserve: без него OpenXml схлопывает ведущие и хвостовые
        // пробелы, и строки отбора вида «Период: … — …» съезжают.
        return new Paragraph(new Run(properties, new Text(value) { Space = SpaceProcessingModeValues.Preserve }));
    }

    private static WordTable Table(IReadOnlyList<string> columns, ReportViewGroup group)
    {
        var table = new WordTable(new TableProperties(
            new TableBorders(
                new TopBorder { Val = BorderValues.Single, Size = 4 },
                new BottomBorder { Val = BorderValues.Single, Size = 4 },
                new LeftBorder { Val = BorderValues.Single, Size = 4 },
                new RightBorder { Val = BorderValues.Single, Size = 4 },
                new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4 },
                new InsideVerticalBorder { Val = BorderValues.Single, Size = 4 }),
            new TableWidth { Width = "5000", Type = TableWidthUnitValues.Pct }));

        var header = new TableRow(
            // Повтор шапки на каждой странице: со второй страницы иначе идут колонки без названий,
            // и таблицу нельзя читать, не листая обратно.
            new TableRowProperties(new TableHeader()));

        foreach (var column in columns)
        {
            header.AppendChild(Cell(column, bold: true));
        }

        table.AppendChild(header);

        foreach (var row in group.Rows)
        {
            var tableRow = new TableRow();
            foreach (var cell in row.Cells)
            {
                tableRow.AppendChild(Cell(cell));
            }

            table.AppendChild(tableRow);
        }

        return table;
    }

    private static TableCell Cell(string value, bool bold = false) =>
        new(Text(value, bold, size: 18));
}
