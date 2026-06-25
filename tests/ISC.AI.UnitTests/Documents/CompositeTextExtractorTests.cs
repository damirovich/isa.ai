using System.Text;
using ISC.AI.Abstractions.Documents;
using ISC.AI.Documents.Extraction;
using Shouldly;

namespace ISC.AI.UnitTests.Documents;

/// <summary>Фасад: маршрутизация по расширению (регистронезависимо) и явный отказ для неподдержанного формата.</summary>
public sealed class CompositeTextExtractorTests
{
    private static CompositeTextExtractor Build() =>
        new([new PlainTextExtractor(), new DocxTextExtractor()]);

    [Fact(DisplayName = "Фасад: поддержку определяет по расширению, регистронезависимо")]
    public void CanExtract_by_extension_case_insensitive()
    {
        var composite = Build();

        composite.CanExtract("doc.txt").ShouldBeTrue();
        composite.CanExtract("doc.DOCX").ShouldBeTrue();
        composite.CanExtract("scan.pdf").ShouldBeFalse();
        composite.CanExtract("noext").ShouldBeFalse();
    }

    [Fact(DisplayName = "Фасад: маршрутизирует .txt в нужный извлекатель")]
    public async Task Routes_txt()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("привет"));

        var result = await Build().ExtractAsync(stream, "a.txt");

        result.Text.ShouldBe("привет");
    }

    [Fact(DisplayName = "Фасад: неподдержанный формат — UnsupportedDocumentFormatException")]
    public async Task Unsupported_throws()
    {
        using var stream = new MemoryStream();

        await Should.ThrowAsync<UnsupportedDocumentFormatException>(
            () => Build().ExtractAsync(stream, "scan.pdf"));
    }
}
