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

    [Fact(DisplayName = "ТБ-033: все три реквизита (гриф, учётный номер, исполнитель) — и в теле, и в метаданных")]
    public async Task All_three_requisites_in_body_and_metadata()
    {
        var bytes = await new DocxDocumentExporter().ExportToDocxAsync(
            new DocumentExportRequest(
                Title: "Справка",
                Body: "Текст.",
                ClassificationMarking: "ДСП",
                Reference: "№ 12-345 от 23.07.2026",
                Executor: "Ашыров Б."));

        using var stream = new MemoryStream(bytes);
        using var document = WordprocessingDocument.Open(stream, isEditable: false);

        // В ТЕЛЕ — все три реквизита.
        var bodyText = document.MainDocumentPart!.Document!.Body!.InnerText;
        bodyText.ShouldContain("ДСП");                    // гриф
        bodyText.ShouldContain("№ 12-345 от 23.07.2026"); // учётный номер
        bodyText.ShouldContain("Ашыров Б.");              // исполнитель

        // В МЕТАДАННЫХ — все три реквизита.
        document.PackageProperties.Category.ShouldBe("ДСП");                     // гриф
        document.PackageProperties.Identifier.ShouldBe("№ 12-345 от 23.07.2026"); // учётный номер
        document.PackageProperties.Creator.ShouldBe("Ашыров Б.");                // исполнитель
    }

    [Fact(DisplayName = "ТБ-033: без номера/исполнителя — явные плейсхолдеры (маркировка структурно полна)")]
    public async Task Missing_requisites_marked_with_placeholders()
    {
        var bytes = await new DocxDocumentExporter().ExportToDocxAsync(
            new DocumentExportRequest(Title: "Справка", Body: "Текст.", ClassificationMarking: "ДСП"));

        using var stream = new MemoryStream(bytes);
        using var document = WordprocessingDocument.Open(stream, isEditable: false);

        var bodyText = document.MainDocumentPart!.Document!.Body!.InnerText;
        bodyText.ShouldContain("не присвоен");  // учётный номер — плейсхолдер
        bodyText.ShouldContain("не указан");    // исполнитель — плейсхолдер
        document.PackageProperties.Identifier.ShouldBe("не присвоен");
        document.PackageProperties.Creator.ShouldBe("не указан");
    }
}
