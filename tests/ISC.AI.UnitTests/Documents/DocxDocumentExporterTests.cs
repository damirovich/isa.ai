using DocumentFormat.OpenXml.Packaging;
using ISC.AI.Abstractions.Documents;
using ISC.AI.Documents.Export;
using Shouldly;

namespace ISC.AI.UnitTests.Documents;

/// <summary>Экспорт .docx (Э4-04, ТБ-033): маркировка грифа обязана быть и в теле, и в метаданных файла.</summary>
public sealed class DocxDocumentExporterTests
{
    [Fact(DisplayName = "Экспорт .docx: гриф проставлен в теле и в метаданных; заголовок и текст присутствуют")]
    public async Task Marking_in_body_and_metadata()
    {
        var bytes = await new DocxDocumentExporter().ExportToDocxAsync(
            new DocumentExportRequest(
                Title: "Тестовая справка",
                Body: "Первый абзац.\nВторой абзац.",
                ClassificationMarking: "ДСП",
                DraftNotice: "ЧЕРНОВИК"));

        bytes.ShouldNotBeEmpty();

        using var stream = new MemoryStream(bytes);
        using var document = WordprocessingDocument.Open(stream, isEditable: false);

        var bodyText = document.MainDocumentPart!.Document!.Body!.InnerText;
        bodyText.ShouldContain("ДСП");               // гриф — в теле
        bodyText.ShouldContain("Тестовая справка");
        bodyText.ShouldContain("Первый абзац.");
        bodyText.ShouldContain("Второй абзац.");

        document.PackageProperties.Category.ShouldBe("ДСП");   // гриф — в метаданных
        document.PackageProperties.Title.ShouldBe("Тестовая справка");
    }
}
