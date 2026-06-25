using ISC.AI.Abstractions.Documents;
using ISC.AI.Abstractions.Ingestion;
using ISC.AI.Ingestion;
using NSubstitute;
using Shouldly;

namespace ISC.AI.UnitTests.Ingestion;

/// <summary>
/// Оркестрация загрузки из файла (Э4-01): неподдержанный формат — отказ без обращения к порту;
/// поддержанный — извлечённый текст и метаданные передаются в <see cref="IIngestionPort"/>.
/// Fail-closed по грифу проверяет сам порт — здесь не дублируется.
/// </summary>
public sealed class FileIngestionServiceTests
{
    [Fact(DisplayName = "Файл-ingestion: неподдержанный формат — отказ, порт не вызывается")]
    public async Task Unsupported_format_rejects_without_port()
    {
        var extractor = Substitute.For<ITextExtractor>();
        extractor.CanExtract("x.pdf").Returns(false);
        var port = Substitute.For<IIngestionPort>();

        var result = await new FileIngestionService(extractor, port)
            .IngestFileAsync(new FileIngestionRequest(new MemoryStream(), "x.pdf", "приказ", Classification: 0, DivisionId: 7));

        result.Accepted.ShouldBeFalse();
        await port.DidNotReceive().IngestAsync(Arg.Any<IngestionRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Файл-ingestion: извлечённый текст и метаданные передаются в порт; заголовок — из извлечённого")]
    public async Task Extracts_and_forwards_to_port()
    {
        var extractor = Substitute.For<ITextExtractor>();
        extractor.CanExtract("doc.docx").Returns(true);
        extractor.ExtractAsync(Arg.Any<Stream>(), "doc.docx", Arg.Any<CancellationToken>())
            .Returns(new ExtractedDocument("текст документа", Title: "Из метаданных"));

        IngestionRequest? captured = null;
        var port = Substitute.For<IIngestionPort>();
        port.IngestAsync(Arg.Do<IngestionRequest>(r => captured = r), Arg.Any<CancellationToken>())
            .Returns(IngestionResult.Ok(documentId: 42, chunkCount: 3));

        var result = await new FileIngestionService(extractor, port)
            .IngestFileAsync(new FileIngestionRequest(new MemoryStream(), "doc.docx", "положение", Classification: 1, DivisionId: 7));

        result.Accepted.ShouldBeTrue();
        captured.ShouldNotBeNull();
        captured!.Text.ShouldBe("текст документа");
        captured.Title.ShouldBe("Из метаданных");
        captured.DocType.ShouldBe("положение");
        captured.Classification.ShouldBe((short?)1);
        captured.DivisionId.ShouldBe(7);
    }
}
