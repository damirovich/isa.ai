using System.Text;
using ISC.AI.Documents.Extraction;
using NSubstitute;
using Shouldly;

namespace ISC.AI.UnitTests.Documents;

/// <summary>
/// Извлечение текста из PDF (PdfPig; индексация файлов документооборота в корпус, этап 7 Э4-35).
/// Страница с текстовым слоем читается напрямую (OCR не трогается); страница-скан распознаётся
/// через <see cref="IImageOcr"/> (ПОДГ-02) — в тестах он подставной, настоящий Tesseract проверяется
/// на стенде оператора. Оба PDF собираются вручную минимальной структурой (сборка через PDFsharp
/// потребовала бы разрешения шрифтов ОС): текстовый — чистым ASCII, скан — с бинарным JPEG в потоке
/// XObject с фильтром DCTDecode, ровно как кладут изображение сканеры.
/// </summary>
public sealed class PdfTextExtractorTests
{
    // Валидный JPEG 1x1 — модель страницы-скана (сканеры кладут JPEG в PDF без перекодирования).
    private static readonly byte[] OnePixelJpeg = Convert.FromBase64String(
        "/9j/4AAQSkZJRgABAQEAYABgAAD/2wBDAAgGBgcGBQgHBwcJCQgKDBQNDAsLDBkSEw8UHRofHh0aHBwgJC4nICIsIxwc"
        + "KDcpLDAxNDQ0Hyc5PTgyPC4zNDL/2wBDAQkJCQwLDBgNDRgyIRwhMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIy"
        + "MjIyMjIyMjIyMjIyMjIyMjL/wAARCAABAAEDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL"
        + "/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJico"
        + "KSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKz"
        + "tLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/9oADAMBAAIRAxEAPwD3+iiigD//2Q==");

    [Fact(DisplayName = "PdfTextExtractor: извлекает текст страницы и заголовок; OCR не вызывается")]
    public async Task Extracts_page_text_and_title()
    {
        var ocr = Substitute.For<IImageOcr>();
        using var pdf = BuildTextPdf(["Inventory report 2026"], title: "Warehouse audit");

        var result = await new PdfTextExtractor(ocr).ExtractAsync(pdf);

        result.Text.ShouldContain("Inventory report 2026");
        result.Title.ShouldBe("Warehouse audit");
        await ocr.DidNotReceiveWithAnyArgs().RecognizeAsync(default!, default);
    }

    [Fact(DisplayName = "ТО-мат-03: страницы PDF разделены пустой строкой (\\n\\n) — границей абзаца для чанкера")]
    public async Task Pages_are_separated_by_blank_line()
    {
        using var pdf = BuildTextPdf(["First page line", "Second page line"]);

        var result = await new PdfTextExtractor(Substitute.For<IImageOcr>()).ExtractAsync(pdf);

        var paragraphs = result.Text.Split(["\n\n"], StringSplitOptions.RemoveEmptyEntries);
        paragraphs.Length.ShouldBe(2);
        paragraphs[0].ShouldContain("First page line");
        paragraphs[1].ShouldContain("Second page line");
    }

    [Fact(DisplayName = "ПОДГ-02: страница-скан (изображение без текстового слоя) распознаётся через OCR")]
    public async Task Scanned_page_is_recognized_via_ocr()
    {
        byte[]? received = null;
        var ocr = Substitute.For<IImageOcr>();
        ocr.RecognizeAsync(Arg.Do<byte[]>(b => received = b), Arg.Any<CancellationToken>())
            .Returns("ПРИКАЗ О ПРОВЕДЕНИИ ПРОВЕРКИ");
        using var pdf = BuildScannedPdf();

        var result = await new PdfTextExtractor(ocr).ExtractAsync(pdf);

        result.Text.ShouldContain("ПРИКАЗ О ПРОВЕДЕНИИ ПРОВЕРКИ");
        received.ShouldNotBeNull();
        // Сканеры кладут JPEG в PDF как есть (DCTDecode) — OCR обязан получить сам JPEG (маркер SOI).
        received![0].ShouldBe((byte)0xFF);
        received[1].ShouldBe((byte)0xD8);
    }

    [Fact(DisplayName = "ПОДГ-02: PDF-скан при ненастроенном OCR → явная ошибка, а не пустой результат")]
    public async Task Scanned_pdf_with_unconfigured_ocr_fails_explicitly()
    {
        // Настоящая реализация IImageOcr без tessdata — сквозная проверка пары извлекателей.
        using var ocr = new TesseractOcrTextExtractor(OcrOptions.NotConfigured);
        using var pdf = BuildScannedPdf();

        var exception = await Should.ThrowAsync<InvalidOperationException>(
            () => new PdfTextExtractor(ocr).ExtractAsync(pdf));
        exception.Message.ShouldContain("OCR не настроен");
    }

    [Fact(DisplayName = "PDF без текста и без изображений даёт ПУСТОЙ текст — решение принимает вызывающий")]
    public async Task Pdf_without_text_and_images_yields_empty_text()
    {
        using var pdf = BuildTextPdf([string.Empty]);

        var result = await new PdfTextExtractor(Substitute.For<IImageOcr>()).ExtractAsync(pdf);

        result.Text.ShouldBeEmpty();
    }

    /// <summary>
    /// PDF-скан: одна страница, всё содержимое — JPEG-изображение (XObject, /Filter /DCTDecode,
    /// байты JPEG как есть — так сохраняют сканеры), текстового слоя нет. Собирается побайтово:
    /// ASCII-объекты + бинарный поток изображения, смещения xref — по фактическим байтам.
    /// </summary>
    private static MemoryStream BuildScannedPdf()
    {
        var content = Encoding.ASCII.GetBytes("q 612 0 0 792 0 0 cm /Im1 Do Q");
        var output = new MemoryStream();
        var offsets = new List<long>();

        void Write(string ascii) => output.Write(Encoding.ASCII.GetBytes(ascii));
        void BeginObj(int number)
        {
            offsets.Add(output.Length);
            Write($"{number} 0 obj\n");
        }

        Write("%PDF-1.4\n");
        BeginObj(1);
        Write("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        BeginObj(2);
        Write("<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        BeginObj(3);
        Write("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
            + "/Resources << /XObject << /Im1 5 0 R >> >> /Contents 4 0 R >>\nendobj\n");
        BeginObj(4);
        Write($"<< /Length {content.Length} >>\nstream\n");
        output.Write(content);
        Write("\nendstream\nendobj\n");
        BeginObj(5);
        Write("<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceRGB "
            + $"/BitsPerComponent 8 /Filter /DCTDecode /Length {OnePixelJpeg.Length} >>\nstream\n");
        output.Write(OnePixelJpeg);
        Write("\nendstream\nendobj\n");

        var xrefPosition = output.Length;
        Write($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            Write(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n"));
        }

        Write($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xrefPosition}\n%%EOF");
        output.Position = 0;
        return output;
    }

    /// <summary>
    /// Минимальный корректный PDF: каталог, дерево страниц, Helvetica, по одному текстовому
    /// оператору на страницу. Только ASCII — смещения xref совпадают с байтовыми.
    /// </summary>
    private static MemoryStream BuildTextPdf(string[] pageTexts, string? title = null)
    {
        var count = pageTexts.Length;
        var objects = new List<string>();

        var kids = string.Join(" ", Enumerable.Range(0, count).Select(i => $"{4 + i} 0 R"));
        objects.Add("<< /Type /Catalog /Pages 2 0 R >>");
        objects.Add($"<< /Type /Pages /Kids [{kids}] /Count {count} >>");
        objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");

        for (var i = 0; i < count; i++)
        {
            objects.Add(
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                + $"/Resources << /Font << /F1 3 0 R >> >> /Contents {4 + count + i} 0 R >>");
        }

        for (var i = 0; i < count; i++)
        {
            var stream = pageTexts[i].Length == 0
                ? string.Empty
                : $"BT /F1 12 Tf 72 720 Td ({pageTexts[i]}) Tj ET";
            objects.Add($"<< /Length {stream.Length} >>\nstream\n{stream}\nendstream");
        }

        var infoRef = string.Empty;
        if (title is not null)
        {
            objects.Add($"<< /Title ({title}) >>");
            infoRef = $" /Info {objects.Count} 0 R";
        }

        var builder = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(builder.Length);
            builder.Append(System.Globalization.CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        var xrefPosition = builder.Length;
        builder.Append(System.Globalization.CultureInfo.InvariantCulture, $"xref\n0 {objects.Count + 1}\n");
        builder.Append("0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            builder.Append(System.Globalization.CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        }

        builder.Append(System.Globalization.CultureInfo.InvariantCulture,
            $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R{infoRef} >>\nstartxref\n{xrefPosition}\n%%EOF");

        return new MemoryStream(Encoding.ASCII.GetBytes(builder.ToString()));
    }
}
