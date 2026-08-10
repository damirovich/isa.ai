using System.Text;
using ISC.AI.Documents.Extraction;
using Shouldly;

namespace ISC.AI.UnitTests.Documents;

/// <summary>
/// Извлечение текста из PDF с текстовым слоем (PdfPig; индексация файлов документооборота в корпус,
/// этап 7 Э4-35). PDF для проверок собирается вручную минимальной ASCII-структурой — сборка через
/// PDFsharp потребовала бы разрешения шрифтов ОС, а проверяется здесь ЧТЕНИЕ, а не запись.
/// </summary>
public sealed class PdfTextExtractorTests
{
    [Fact(DisplayName = "PdfTextExtractor: извлекает текст страницы и заголовок из метаданных")]
    public async Task Extracts_page_text_and_title()
    {
        using var pdf = BuildPdf(["Inventory report 2026"], title: "Warehouse audit");

        var result = await new PdfTextExtractor().ExtractAsync(pdf);

        result.Text.ShouldContain("Inventory report 2026");
        result.Title.ShouldBe("Warehouse audit");
    }

    [Fact(DisplayName = "ТО-мат-03: страницы PDF разделены пустой строкой (\\n\\n) — границей абзаца для чанкера")]
    public async Task Pages_are_separated_by_blank_line()
    {
        using var pdf = BuildPdf(["First page line", "Second page line"]);

        var result = await new PdfTextExtractor().ExtractAsync(pdf);

        var paragraphs = result.Text.Split(["\n\n"], StringSplitOptions.RemoveEmptyEntries);
        paragraphs.Length.ShouldBe(2);
        paragraphs[0].ShouldContain("First page line");
        paragraphs[1].ShouldContain("Second page line");
    }

    [Fact(DisplayName = "Скан без текстового слоя даёт ПУСТОЙ текст — решение принимает вызывающий, не извлекатель")]
    public async Task Scanned_pdf_without_text_layer_yields_empty_text()
    {
        // Страница без текстовых операторов — модель скана: картинка есть, текстового слоя нет.
        using var pdf = BuildPdf([string.Empty]);

        var result = await new PdfTextExtractor().ExtractAsync(pdf);

        result.Text.ShouldBeEmpty();
    }

    /// <summary>
    /// Минимальный корректный PDF: каталог, дерево страниц, Helvetica, по одному текстовому
    /// оператору на страницу. Только ASCII — смещения xref совпадают с байтовыми.
    /// </summary>
    private static MemoryStream BuildPdf(string[] pageTexts, string? title = null)
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
