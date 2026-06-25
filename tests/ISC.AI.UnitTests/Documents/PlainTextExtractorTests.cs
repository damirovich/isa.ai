using System.Text;
using ISC.AI.Documents.Extraction;
using Shouldly;

namespace ISC.AI.UnitTests.Documents;

/// <summary>Извлечение простого текста (.txt) с обрезкой краевых пробелов.</summary>
public sealed class PlainTextExtractorTests
{
    [Fact(DisplayName = "PlainTextExtractor: читает UTF-8 и обрезает края")]
    public async Task Reads_utf8_and_trims()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("  текст документа  "));

        var result = await new PlainTextExtractor().ExtractAsync(stream);

        result.Text.ShouldBe("текст документа");
    }
}
