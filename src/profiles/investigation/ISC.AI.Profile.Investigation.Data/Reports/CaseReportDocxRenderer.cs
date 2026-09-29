using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;

namespace ISC.AI.Profile.Investigation.Data.Reports;

/// <summary>
/// Выгрузка сводки или справки в .docx (ТФ-ДДЛ-04, ADR-0031 п. 7). Файл строится из полей редакции при каждой
/// выгрузке — отдельно не хранится.
/// </summary>
/// <remarks>
/// ТБ-033: гриф — первой строкой справа жирным И в свойствах файла (Category/Keywords), чтобы маркировка
/// пережила копирование текста; исполнитель — в конце и в Creator. У сводки таблица событий с повторяемой
/// шапкой: со второй страницы читатель видит названия колонок. Бланк Заказчика (.docx) подключается позже —
/// до его получения вёрстка единая.
/// </remarks>
public sealed class CaseReportDocxRenderer : ICaseReportRenderer
{
    /// <inheritdoc />
    public byte[] RenderDocx(CaseReportExport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var title = $"{report.Kind.Label()} за {report.ReportDate.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)}";

        using var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            main.Document = new Document();
            var body = main.Document.AppendChild(new Body());

            body.AppendChild(Paragraph(report.ClassificationMarking, bold: true, align: JustificationValues.Right));
            body.AppendChild(Paragraph(title, bold: true, size: 32, align: JustificationValues.Center));
            body.AppendChild(Paragraph($"Дело № {report.CaseNumber}"));
            if (!string.IsNullOrWhiteSpace(report.ObjectName))
            {
                body.AppendChild(Paragraph($"Объект: {report.ObjectName}"));
            }

            body.AppendChild(Paragraph($"Редакция № {report.Revision.ToString(CultureInfo.InvariantCulture)}"));

            var content = report.Content;
            if (report.Kind == CaseReportKind.SummaryOn)
            {
                body.AppendChild(Paragraph("Хронология событий", bold: true, size: 26));
                body.AppendChild(EventsTable(content.EventRows));
            }
            else
            {
                Section(body, "Установочные данные", content.Identity);
                Section(body, "Адреса", content.Addresses);
                Section(body, "Род занятий", content.Occupation);
                Section(body, "Семейные и иные связи", content.Family);
                Section(body, "Характеризующие материалы", content.Characterizing);
            }

            Section(body, "Вывод", content.Conclusion);
            body.AppendChild(Paragraph($"Исполнитель: {report.Executor}"));

            main.Document.Save();

            document.PackageProperties.Title = title;
            document.PackageProperties.Category = report.ClassificationMarking;
            document.PackageProperties.Keywords = report.ClassificationMarking;
            document.PackageProperties.Creator = report.Executor;
        }

        return stream.ToArray();
    }

    private static void Section(Body body, string heading, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        body.AppendChild(Paragraph(heading, bold: true, size: 26));
        foreach (var line in text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n'))
        {
            body.AppendChild(Paragraph(line));
        }
    }

    private static Table EventsTable(IReadOnlyList<SummaryEvent> events)
    {
        var table = new Table(new TableProperties(
            new TableWidth { Type = TableWidthUnitValues.Pct, Width = "5000" },
            new TableBorders(
                new TopBorder { Val = BorderValues.Single, Size = 4 },
                new BottomBorder { Val = BorderValues.Single, Size = 4 },
                new LeftBorder { Val = BorderValues.Single, Size = 4 },
                new RightBorder { Val = BorderValues.Single, Size = 4 },
                new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4 },
                new InsideVerticalBorder { Val = BorderValues.Single, Size = 4 })));

        var header = new TableRow(new TableRowProperties(new TableHeader()));
        foreach (var column in new[] { "№", "Время", "Место / адрес", "Событие, маршрут", "Лица", "Транспорт" })
        {
            header.AppendChild(Cell(column, bold: true));
        }

        table.AppendChild(header);

        var number = 0;
        foreach (var e in events)
        {
            number++;
            table.AppendChild(new TableRow(
                Cell(number.ToString(CultureInfo.InvariantCulture)),
                Cell(e.Time), Cell(e.Place), Cell(e.Description), Cell(e.Persons), Cell(e.Vehicles)));
        }

        if (number == 0)
        {
            table.AppendChild(new TableRow(Cell("—"), Cell(null), Cell(null), Cell("Событий нет"), Cell(null), Cell(null)));
        }

        return table;
    }

    private static TableCell Cell(string? text, bool bold = false) =>
        new(Paragraph(text ?? string.Empty, bold));

    private static Paragraph Paragraph(string text, bool bold = false, int? size = null, JustificationValues? align = null)
    {
        var runProperties = new RunProperties();
        if (bold)
        {
            runProperties.AppendChild(new Bold());
        }

        if (size is { } halfPoints)
        {
            runProperties.AppendChild(new FontSize { Val = halfPoints.ToString(CultureInfo.InvariantCulture) });
        }

        var paragraph = new Paragraph();
        if (align is { } justification)
        {
            paragraph.AppendChild(new ParagraphProperties(new Justification { Val = justification }));
        }

        paragraph.AppendChild(new Run(runProperties, new Text(text) { Space = SpaceProcessingModeValues.Preserve }));
        return paragraph;
    }
}
