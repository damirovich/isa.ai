using System.Globalization;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using ISC.AI.Profile.Investigation.Domain.Services;

namespace ISC.AI.Profile.Investigation.Data.Reports;

/// <summary>
/// Справка по делу или по фигуранту в .docx (ТФ-ОТЧ-01/02, ADR-0053). Файл строится при каждой выгрузке, отдельно не
/// хранится.
/// </summary>
/// <remarks>
/// <para>ТБ-033: гриф — первой строкой справа жирным, последней строкой и в свойствах файла (Category/Keywords);
/// «Учётный номер: не присвоен» — в теле и в Identifier; исполнитель — в конце и в Creator. Пометка черновика — под
/// грифом (ТБ-042).</para>
/// <para>Изложение модели — отдельным разделом с пометкой, кем подготовлено; таблицы фактов — расчёт системы. Таблицы с
/// повторяемой шапкой: со второй страницы видны названия колонок. Управляющие символы, недопустимые в XML, из текста
/// вырезаются — иначе файл не сохранится.</para>
/// </remarks>
public sealed class BriefDocxRenderer : IBriefRenderer
{
    /// <inheritdoc />
    public byte[] RenderDocx(BriefExport document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var brief = document.Brief;

        using var stream = new MemoryStream();
        using (var word = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var main = word.AddMainDocumentPart();
            main.Document = new Document();
            var body = main.Document.AppendChild(new Body());

            body.AppendChild(Paragraph(document.ClassificationMarking, bold: true, align: JustificationValues.Right));
            body.AppendChild(Paragraph("Учётный номер: не присвоен", align: JustificationValues.Right));
            body.AppendChild(Paragraph(document.DraftNotice, bold: true, italic: true, align: JustificationValues.Center));
            body.AppendChild(Paragraph(brief.Title, bold: true, size: 32, align: JustificationValues.Center));
            if (!string.IsNullOrWhiteSpace(brief.Subtitle))
            {
                body.AppendChild(Paragraph(brief.Subtitle, align: JustificationValues.Center));
            }

            if (!string.IsNullOrWhiteSpace(document.Summary))
            {
                body.AppendChild(Paragraph("Краткое изложение", bold: true, size: 26));
                body.AppendChild(Paragraph(document.SummaryNotice, italic: true));
                foreach (var line in document.Summary.Split('\n'))
                {
                    body.AppendChild(Paragraph(line));
                }
            }

            foreach (var section in brief.Sections)
            {
                body.AppendChild(Paragraph(section.Title, bold: true, size: 26));
                if (section.IsEmpty)
                {
                    body.AppendChild(Paragraph("Сведений нет."));
                    continue;
                }

                foreach (var fact in section.Facts)
                {
                    body.AppendChild(FactParagraph(fact));
                }

                if (section.Table is { TotalRows: > 0 } table)
                {
                    body.AppendChild(Table(table));
                    if (table.Omitted > 0)
                    {
                        body.AppendChild(Paragraph(
                            string.Create(CultureInfo.InvariantCulture, $"Не вошло в таблицу: {table.Omitted} из {table.TotalRows}."),
                            italic: true));
                    }
                }

                if (!string.IsNullOrWhiteSpace(section.Note))
                {
                    body.AppendChild(Paragraph(section.Note, italic: true));
                }
            }

            body.AppendChild(Paragraph($"Исполнитель: {document.Executor}"));
            body.AppendChild(Paragraph(document.ClassificationMarking, bold: true, align: JustificationValues.Right));

            main.Document.Save();

            word.PackageProperties.Title = Clean(brief.Title);
            word.PackageProperties.Category = document.ClassificationMarking;
            word.PackageProperties.Keywords = document.ClassificationMarking;
            word.PackageProperties.Creator = Clean(document.Executor);
            word.PackageProperties.Identifier = "не присвоен";
        }

        return stream.ToArray();
    }

    private static Paragraph FactParagraph(BriefFact fact)
    {
        var paragraph = new Paragraph();
        paragraph.AppendChild(new Run(new RunProperties(new Bold()), new Text(Clean(fact.Label) + ": ") { Space = SpaceProcessingModeValues.Preserve }));
        paragraph.AppendChild(new Run(new Text(Clean(fact.Value)) { Space = SpaceProcessingModeValues.Preserve }));
        return paragraph;
    }

    private static Table Table(BriefTable source)
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
        foreach (var column in source.Columns)
        {
            header.AppendChild(Cell(column, bold: true));
        }

        table.AppendChild(header);
        foreach (var row in source.Rows)
        {
            var tableRow = new TableRow();
            for (var i = 0; i < source.Columns.Count; i++)
            {
                tableRow.AppendChild(Cell(i < row.Count ? row[i] : string.Empty));
            }

            table.AppendChild(tableRow);
        }

        return table;
    }

    private static TableCell Cell(string? text, bool bold = false) =>
        new(new TableCellProperties(), Paragraph(text ?? string.Empty, bold, size: 18));

    private static Paragraph Paragraph(string text, bool bold = false, bool italic = false, int? size = null, JustificationValues? align = null)
    {
        var runProperties = new RunProperties();
        if (bold)
        {
            runProperties.AppendChild(new Bold());
        }

        if (italic)
        {
            runProperties.AppendChild(new Italic());
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

        paragraph.AppendChild(new Run(runProperties, new Text(Clean(text)) { Space = SpaceProcessingModeValues.Preserve }));
        return paragraph;
    }

    /// <summary>
    /// Текст без символов, недопустимых в XML 1.0 (управляющие, кроме табуляции, и половинки суррогатных пар), и без
    /// переводов строки внутри абзаца: текст модели и введённый людьми может их содержать, а OpenXml такой документ не
    /// сохранит.
    /// </summary>
    public static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '\r' or '\n')
            {
                sb.Append(' ');
            }
            else if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                // Целая суррогатная пара (эмодзи и др.) — допустима.
                sb.Append(c).Append(text[i + 1]);
                i++;
            }
            else if (char.IsSurrogate(c))
            {
                // Половина пары недопустима в XML: запись файла упала бы.
            }
            else if (c == '\t' || (c >= 0x20 && c != 0xFFFE && c != 0xFFFF))
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}
