using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using ISC.AI.Documents.Extraction;
using Shouldly;

namespace ISC.AI.UnitTests.Documents;

/// <summary>Извлечение текста из .docx (OpenXml): абзацы и заголовок из метаданных файла (Э4-01).</summary>
public sealed class DocxTextExtractorTests
{
    [Fact(DisplayName = "DocxTextExtractor: извлекает текст абзацев и заголовок из .docx")]
    public async Task Extracts_paragraphs_and_title()
    {
        using var docx = BuildDocx(["Первый абзац.", "Второй абзац."], title: "Тестовый документ");

        var result = await new DocxTextExtractor().ExtractAsync(docx);

        result.Text.ShouldContain("Первый абзац.");
        result.Text.ShouldContain("Второй абзац.");
        result.Title.ShouldBe("Тестовый документ");
    }

    private static MemoryStream BuildDocx(string[] paragraphs, string title)
    {
        var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            var elements = paragraphs
                .Select(p => (OpenXmlElement)new Paragraph(new Run(new Text(p))))
                .ToArray();
            main.Document = new Document(new Body(elements));
            main.Document.Save();
            doc.PackageProperties.Title = title;
        }

        ms.Position = 0;
        return ms;
    }
}
