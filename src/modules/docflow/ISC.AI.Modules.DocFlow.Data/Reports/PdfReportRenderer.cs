using ISC.AI.Modules.DocFlow.Domain.Services;
using PdfSharp;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace ISC.AI.Modules.DocFlow.Data.Reports;

/// <summary>
/// Отчёт в PDF (PDFsharp, MIT). Альбомный A4, гриф — на каждой странице, шапка таблицы повторяется.
/// </summary>
/// <remarks>
/// PDF — формат «для печати и рассылки», страницы которого легко разлучить друг с другом. Поэтому
/// гриф и номер страницы ставятся на КАЖДОМ листе (ТБ-033): одна вырванная страница без маркировки
/// перестаёт быть узнаваемо режимной. Вёрстка ручная — таблиц у PDFsharp нет, зато нет и внешних
/// зависимостей, требующих интернета.
/// </remarks>
public sealed class PdfReportRenderer : IReportRenderer
{
    // Пропорции колонок (сумма произвольна — считается доля). «Краткое содержание» шире прочих:
    // это единственная колонка со связным текстом, остальные — короткие реквизиты.
    private static readonly double[] ColumnWeights = [10, 8, 12, 26, 12, 12, 12, 8, 10];

    private const double Margin = 28;
    private const double LineHeight = 11;
    private const double CellPadding = 3;

    private readonly PdfReportFontOptions _fontOptions;

    /// <summary>Создаёт рендерер с указанными путями к файлам шрифта.</summary>
    public PdfReportRenderer(PdfReportFontOptions fontOptions)
    {
        _fontOptions = fontOptions ?? throw new ArgumentNullException(nameof(fontOptions));
    }

    /// <inheritdoc />
    public ReportFormat Format => ReportFormat.Pdf;

    /// <inheritdoc />
    public ReportDocument Render(ReportView view)
    {
        ArgumentNullException.ThrowIfNull(view);

        // Резолвер ставится при первой отрисовке, а не в конструкторе: если шрифта нет, ошибку
        // должен получить тот, кто запросил отчёт, а не запуск всего приложения.
        PdfReportFonts.Install(_fontOptions);

        using var document = new PdfDocument();
        document.Info.Title = view.Title;
        document.Info.Subject = view.ClassificationMarking;
        document.Info.Keywords = view.ClassificationMarking;

        var regular = new XFont(PdfReportFontResolver.FamilyName, 7.5, XFontStyleEx.Regular);
        var bold = new XFont(PdfReportFontResolver.FamilyName, 7.5, XFontStyleEx.Bold);
        var titleFont = new XFont(PdfReportFontResolver.FamilyName, 13, XFontStyleEx.Bold);
        var markFont = new XFont(PdfReportFontResolver.FamilyName, 9, XFontStyleEx.Bold);

        var page = NewPage(document, view, markFont, out var gfx, out var y);
        var widths = ColumnWidths(gfx.PageSize.Width - (2 * Margin));

        try
        {
            gfx.DrawString(view.Title, titleFont, XBrushes.Black, Margin, y);
            y += 18;

            foreach (var line in view.FilterLines)
            {
                gfx.DrawString(line, regular, XBrushes.Black, Margin, y);
                y += LineHeight;
            }

            gfx.DrawString(view.PrintStamp, regular, XBrushes.Black, Margin, y);
            y += LineHeight * 2;

            foreach (var group in view.Groups)
            {
                y = EnsureSpace(document, view, markFont, ref gfx, y, needed: 40);

                gfx.DrawString(group.Title, bold, XBrushes.Black, Margin, y);
                y += LineHeight + 2;

                y = DrawRow(gfx, view.Columns, widths, y, bold, header: true);

                foreach (var row in group.Rows)
                {
                    var height = RowHeight(gfx, row.Cells, widths, regular);
                    var moved = EnsureSpace(document, view, markFont, ref gfx, y, height);
                    if (moved != y)
                    {
                        // После переноса на новую страницу шапка таблицы рисуется заново — иначе
                        // читатель видит колонки без названий и не может понять, что перед ним.
                        y = DrawRow(gfx, view.Columns, widths, moved, bold, header: true);
                    }

                    y = DrawRow(gfx, row.Cells, widths, y, regular, header: false);
                }

                y += 4;
                foreach (var total in group.Totals)
                {
                    y = EnsureSpace(document, view, markFont, ref gfx, y, LineHeight);
                    gfx.DrawString($"{total.Label}: {total.Value}", regular, XBrushes.Black, Margin, y);
                    y += LineHeight;
                }

                y += LineHeight;
            }

            y = EnsureSpace(document, view, markFont, ref gfx, y, (view.Totals.Count + 1) * LineHeight);
            gfx.DrawString("ИТОГО", bold, XBrushes.Black, Margin, y);
            y += LineHeight + 2;

            foreach (var total in view.Totals)
            {
                gfx.DrawString($"{total.Label}: {total.Value}", regular, XBrushes.Black, Margin, y);
                y += LineHeight;
            }
        }
        finally
        {
            gfx.Dispose();
        }

        NumberPages(document, view);

        using var stream = new MemoryStream();
        document.Save(stream);

        return new ReportDocument(
            stream.ToArray(),
            ReportFileNames.For(view.Title, "pdf"),
            "application/pdf");
    }

    private static PdfPage NewPage(
        PdfDocument document, ReportView view, XFont markFont, out XGraphics gfx, out double y)
    {
        var page = document.AddPage();
        page.Size = PageSize.A4;
        page.Orientation = PageOrientation.Landscape;

        gfx = XGraphics.FromPdfPage(page);

        // Гриф — в самом верху каждой страницы (ТБ-033).
        gfx.DrawString(view.ClassificationMarking, markFont, XBrushes.DarkRed, Margin, Margin);
        y = Margin + 22;

        return page;
    }

    /// <summary>Переносит на новую страницу, если запрошенная высота не помещается.</summary>
    private static double EnsureSpace(
        PdfDocument document, ReportView view, XFont markFont, ref XGraphics gfx, double y, double needed)
    {
        var bottom = gfx.PageSize.Height - Margin - LineHeight;
        if (y + needed <= bottom)
        {
            return y;
        }

        gfx.Dispose();
        NewPage(document, view, markFont, out gfx, out var top);
        return top;
    }

    private static double[] ColumnWidths(double available)
    {
        var sum = ColumnWeights.Sum();
        return [.. ColumnWeights.Select(weight => available * weight / sum)];
    }

    private static double RowHeight(XGraphics gfx, IReadOnlyList<string> cells, double[] widths, XFont font)
    {
        var lines = 1;
        for (var i = 0; i < cells.Count && i < widths.Length; i++)
        {
            lines = Math.Max(lines, Wrap(gfx, cells[i], font, widths[i] - (2 * CellPadding)).Count);
        }

        return (lines * LineHeight) + (2 * CellPadding);
    }

    private static double DrawRow(
        XGraphics gfx, IReadOnlyList<string> cells, double[] widths, double y, XFont font, bool header)
    {
        var height = RowHeight(gfx, cells, widths, font);
        var x = Margin;

        if (header)
        {
            gfx.DrawRectangle(XBrushes.LightGray, x, y, widths.Sum(), height);
        }

        for (var i = 0; i < cells.Count && i < widths.Length; i++)
        {
            gfx.DrawRectangle(XPens.LightGray, x, y, widths[i], height);

            var textY = y + CellPadding;
            foreach (var line in Wrap(gfx, cells[i], font, widths[i] - (2 * CellPadding)))
            {
                gfx.DrawString(line, font, XBrushes.Black, x + CellPadding, textY + LineHeight - 3);
                textY += LineHeight;
            }

            x += widths[i];
        }

        return y + height;
    }

    /// <summary>
    /// Разбивает текст по словам под ширину колонки.
    /// </summary>
    /// <remarks>
    /// Слово длиннее колонки (например, рег. номер без пробелов) режется по символам: иначе оно
    /// выходит за границу ячейки и наползает на соседнюю — в PDF обрезки по границе нет.
    /// </remarks>
    private static List<string> Wrap(XGraphics gfx, string text, XFont font, double width)
    {
        if (string.IsNullOrEmpty(text) || width <= 0)
        {
            return [string.Empty];
        }

        var lines = new List<string>();
        var current = string.Empty;

        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = current.Length == 0 ? word : $"{current} {word}";
            if (gfx.MeasureString(candidate, font).Width <= width)
            {
                current = candidate;
                continue;
            }

            if (current.Length > 0)
            {
                lines.Add(current);
                current = string.Empty;
            }

            var rest = word;
            while (gfx.MeasureString(rest, font).Width > width && rest.Length > 1)
            {
                var take = rest.Length;
                while (take > 1 && gfx.MeasureString(rest[..take], font).Width > width)
                {
                    take--;
                }

                lines.Add(rest[..take]);
                rest = rest[take..];
            }

            current = rest;
        }

        lines.Add(current);
        return lines;
    }

    /// <summary>Нумерация «страница N из M» — известна только после отрисовки всех страниц.</summary>
    private static void NumberPages(PdfDocument document, ReportView view)
    {
        var font = new XFont(PdfReportFontResolver.FamilyName, 7.5, XFontStyleEx.Regular);

        for (var i = 0; i < document.PageCount; i++)
        {
            var page = document.Pages[i];
            using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);

            var text = $"{view.ClassificationMarking} • страница "
                + $"{(i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)} из "
                + document.PageCount.ToString(System.Globalization.CultureInfo.InvariantCulture);

            gfx.DrawString(text, font, XBrushes.Gray, Margin, gfx.PageSize.Height - Margin + 6);
        }
    }
}
