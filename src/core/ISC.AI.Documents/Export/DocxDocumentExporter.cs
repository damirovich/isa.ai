using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using ISC.AI.Abstractions.Documents;

namespace ISC.AI.Documents.Export;

/// <summary>
/// Экспорт в <c>.docx</c> через OpenXml. Учётные реквизиты ТБ-033 проставляются ОБЯЗАТЕЛЬНО и в теле, и в
/// метаданных файла: гриф (в теле — верх справа жирным; в метаданных — Category/Keywords), учётный номер
/// (в теле; в метаданных — Identifier) и исполнитель (в теле; в метаданных — Creator). Отсутствующее
/// значение — явный плейсхолдер, чтобы маркировка была структурно полной.
/// </summary>
public sealed class DocxDocumentExporter : IDocumentExporter
{
    private const string ReferenceUnset = "не присвоен";
    private const string ExecutorUnset = "не указан";

    /// <inheritdoc />
    public Task<byte[]> ExportToDocxAsync(DocumentExportRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            var body = new Body();

            var reference = string.IsNullOrWhiteSpace(request.Reference) ? ReferenceUnset : request.Reference;
            var executor = string.IsNullOrWhiteSpace(request.Executor) ? ExecutorUnset : request.Executor;

            // Учётные реквизиты — В ТЕЛЕ (обязательная маркировка ТБ-033): гриф (верх, справа, жирным),
            // учётный номер под ним; исполнитель — в конце (официальная практика оформления).
            body.Append(Marking(request.ClassificationMarking));
            body.Append(Text($"Учётный номер: {reference}"));

            if (!string.IsNullOrWhiteSpace(request.DraftNotice))
            {
                body.Append(Notice(request.DraftNotice));
            }

            body.Append(Title(request.Title));

            foreach (var line in (request.Body ?? string.Empty).Split('\n'))
            {
                body.Append(Text(line));
            }

            body.Append(Text($"Исполнитель: {executor}"));

            main.Document = new Document(body);
            main.Document.Save();

            // Учётные реквизиты — В МЕТАДАННЫХ файла (ТБ-033): гриф (Category/Keywords), исполнитель
            // (Creator — «кто изготовил»), учётный номер (Identifier — уникальный идентификатор ресурса).
            document.PackageProperties.Title = request.Title;
            document.PackageProperties.Category = request.ClassificationMarking;
            document.PackageProperties.Keywords = request.ClassificationMarking;
            document.PackageProperties.Creator = executor;
            document.PackageProperties.Identifier = reference;
        }

        return Task.FromResult(stream.ToArray());
    }

    private static Paragraph Marking(string text) =>
        new(new ParagraphProperties(new Justification { Val = JustificationValues.Right }),
            new Run(new RunProperties(new Bold()), new Text(text)));

    private static Paragraph Notice(string text) =>
        new(new ParagraphProperties(new Justification { Val = JustificationValues.Center }),
            new Run(new RunProperties(new Bold(), new Italic()), new Text(text)));

    private static Paragraph Title(string text) =>
        new(new ParagraphProperties(new Justification { Val = JustificationValues.Center }),
            new Run(new RunProperties(new Bold()), new Text(text)));

    private static Paragraph Text(string text) =>
        new(new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve }));
}
