using System;
using System.Globalization;
using System.IO;
using ISC.AI.Modules.Media.Domain.Model;
using SkiaSharp;

namespace ISC.AI.Vision.Onnx.Imaging;

/// <summary>
/// Фототаблица в PDF на SkiaSharp (ТФ-МЕД-20, ADR-0057): листы A4 по раскладке <see cref="PhotoSheetLayout"/>,
/// снимки вписаны с сохранением пропорций, подписи — цветом текста на подложке, маркировка и водяной знак.
/// </summary>
/// <remarks>
/// ИНВАРИАНТ (ТБ-033, ТБ-076, ТО-инф-10): на КАЖДОЙ странице — маркировка грифа сверху и снизу, «Выдал: ФИО · дата» и
/// полупрозрачный водяной знак получателя поверх снимков; вырванный лист не теряет ни грифа, ни следа выдачи. Гриф и
/// исполнитель — ещё и в свойствах файла. Снимки только масштабируются (ТЭ-007).
/// PDF формирует Skia, а не PDFsharp: у PDFsharp резолвер шрифтов — глобальное состояние процесса, занятое отчётами
/// документооборота. Шрифт — тот же системный с кириллицей, что у выгрузки с пометками; Skia встраивает его в файл.
/// </remarks>
internal static class PhotoSheetPdf
{
    private const float PointsPerMm = 72f / 25.4f;

    // Водяной знак: серый, прозрачность ≈ 16 % — читается, но не закрывает снимок.
    private static readonly SKColor WatermarkColor = new(110, 110, 110, 40);
    private static readonly SKColor MarkingColor = new(183, 28, 28);
    private static readonly SKColor TextColor = new(33, 33, 33);

    /// <summary>Сформировать PDF.</summary>
    /// <exception cref="InvalidOperationException">Снимок не распознан.</exception>
    public static byte[] Render(PhotoSheetDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var pages = PhotoSheetLayout.Pages(document.Photos.Count, document.PerPage, document.SingleSize);
        var regular = ImageEditing.Typeface();
        var bold = SKFontManager.Default.MatchFamily(regular.FamilyName, SKFontStyle.Bold) ?? regular;
        var metadata = new SKDocumentPdfMetadata
        {
            Title = document.Title,
            Author = document.Executor,
            Subject = document.Marking,
            Keywords = "фототаблица; " + document.Marking,
            Creator = "ISC.AI",
            Producer = "ISC.AI",
            Creation = document.IssuedAt,
            Modified = document.IssuedAt,
            RasterDpi = 300,
            EncodingQuality = 90,
        };

        using var stream = new MemoryStream();
        using (var pdf = SKDocument.CreatePdf(stream, metadata)
            ?? throw new InvalidOperationException("Не удалось создать документ PDF."))
        {
            foreach (var page in pages)
            {
                var canvas = pdf.BeginPage(Pt(PhotoSheetLayout.PageWidthMm), Pt(PhotoSheetLayout.PageHeightMm));
                foreach (var cell in page.Cells)
                {
                    DrawPhoto(canvas, cell, document.Photos[cell.Index], document.PerPage, regular);
                }

                DrawMarking(canvas, document, page.Number, pages.Count, regular, bold);
                DrawWatermark(canvas, document.IssuedBy, regular);
                pdf.EndPage();
            }

            pdf.Close();
        }

        return stream.ToArray();
    }

    private static void DrawPhoto(SKCanvas canvas, PhotoSheetCell cell, PhotoSheetPhoto photo, int perPage, SKTypeface typeface)
    {
        using var image = SKImage.FromEncodedData(photo.Image)
            ?? throw new InvalidOperationException("Снимок не распознан: формат не поддерживается или файл повреждён.");
        var place = PhotoSheetLayout.Fit(cell.Image, image.Width, image.Height);
        canvas.DrawImage(image, Rect(place), new SKSamplingOptions(SKCubicResampler.Mitchell));

        if (string.IsNullOrWhiteSpace(photo.Caption))
        {
            return;
        }

        // Подпись — по ширине ячейки; длинная укорачивается «…», шрифт — по высоте полосы.
        using var background = new SKPaint { Color = SKColor.Parse(photo.BackgroundColor), IsAntialias = true };
        canvas.DrawRect(Rect(cell.Caption), background);
        using var font = new SKFont(typeface, Pt(PhotoSheetLayout.CaptionHeightMm(perPage)) * 0.55f);
        using var paint = new SKPaint { Color = SKColor.Parse(photo.TextColor), IsAntialias = true };
        var padding = Pt(1.5);
        var text = Shorten(photo.Caption.Trim(), font, Pt(cell.Caption.Width) - (2 * padding));
        var baseline = Pt(cell.Caption.Y + (cell.Caption.Height / 2)) + (font.Size * 0.35f);
        canvas.DrawText(text, Pt(cell.Caption.X + (cell.Caption.Width / 2)), baseline, SKTextAlign.Center, font, paint);
    }

    // Две строки сверху и снизу — тексты не делят строку и не обрезают друг друга. Сверху: гриф по центру, под ним
    // заголовок слева и «лист N из M» справа. Снизу: «Выдал: ФИО · дата» целиком (ТБ-076), под ним гриф по центру.
    private static void DrawMarking(SKCanvas canvas, PhotoSheetDocument document, int number, int count, SKTypeface regular, SKTypeface bold)
    {
        var left = Pt(PhotoSheetLayout.MarginMm);
        var right = Pt(PhotoSheetLayout.PageWidthMm - PhotoSheetLayout.MarginMm);
        var center = Pt(PhotoSheetLayout.PageWidthMm / 2);
        var width = right - left;
        var bottom = PhotoSheetLayout.PageHeightMm - PhotoSheetLayout.MarginMm;

        using var markingFont = new SKFont(bold, 10f);
        using var textFont = new SKFont(regular, 8.5f);
        using var marking = new SKPaint { Color = MarkingColor, IsAntialias = true };
        using var text = new SKPaint { Color = TextColor, IsAntialias = true };

        var sheet = string.Create(CultureInfo.InvariantCulture, $"лист {number} из {count}");
        var markingText = Shorten(document.Marking, markingFont, width);
        canvas.DrawText(markingText, center, Pt(PhotoSheetLayout.MarginMm + 4), SKTextAlign.Center, markingFont, marking);
        canvas.DrawText(sheet, right, Pt(PhotoSheetLayout.MarginMm + 10), SKTextAlign.Right, textFont, text);
        var titleWidth = width - textFont.MeasureText(sheet) - Pt(4);
        canvas.DrawText(Shorten(document.Title, textFont, titleWidth), left, Pt(PhotoSheetLayout.MarginMm + 10), SKTextAlign.Left, textFont, text);

        canvas.DrawText(Shorten("Выдал: " + document.IssuedBy, textFont, width), left, Pt(bottom - 7), SKTextAlign.Left, textFont, text);
        canvas.DrawText(markingText, center, Pt(bottom - 1), SKTextAlign.Center, markingFont, marking);
    }

    // Водяной знак получателя (ТБ-076) — поперёк страницы трижды, поверх снимков.
    private static void DrawWatermark(SKCanvas canvas, string issuedBy, SKTypeface typeface)
    {
        using var font = new SKFont(typeface, 22f);
        using var paint = new SKPaint { Color = WatermarkColor, IsAntialias = true };
        var centerX = Pt(PhotoSheetLayout.PageWidthMm / 2);
        foreach (var share in new[] { 0.25, 0.5, 0.75 })
        {
            var centerY = Pt(PhotoSheetLayout.PageHeightMm * share);
            canvas.Save();
            canvas.RotateDegrees(-35, centerX, centerY);
            canvas.DrawText(issuedBy, centerX, centerY, SKTextAlign.Center, font, paint);
            canvas.Restore();
        }
    }

    private static string Shorten(string text, SKFont font, float width)
    {
        if (font.MeasureText(text) <= width)
        {
            return text;
        }

        var length = text.Length;
        while (length > 0 && font.MeasureText(string.Concat(text.AsSpan(0, length), "…")) > width)
        {
            length--;
        }

        return string.Concat(text.AsSpan(0, length).TrimEnd(), "…");
    }

    private static float Pt(double millimetres) => (float)millimetres * PointsPerMm;

    private static SKRect Rect(SheetRect rect) =>
        SKRect.Create(Pt(rect.X), Pt(rect.Y), Pt(rect.Width), Pt(rect.Height));
}
