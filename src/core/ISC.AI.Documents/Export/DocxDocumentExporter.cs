using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using ISC.AI.Abstractions.Documents;

namespace ISC.AI.Documents.Export;

/// <summary>
/// Экспорт в <c>.docx</c> через OpenXml. Маркировка грифа (ТБ-033) проставляется ОБЯЗАТЕЛЬНО:
/// видимой строкой в теле (верх, справа, жирным) и в метаданных файла (Category/Keywords).
/// </summary>
public sealed class DocxDocumentExporter : IDocumentExporter
{
    /// <inheritdoc />
    public Task<byte[]> ExportToDocxAsync(DocumentExportRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
        {
            var main = document.AddMainDocumentPart();
            var body = new Body();

            // Гриф — В ТЕЛЕ (верх, справа, жирным): обязательная маркировка (ТБ-033).
            body.Append(Marking(request.ClassificationMarking));

            if (!string.IsNullOrWhiteSpace(request.DraftNotice))
            {
                body.Append(Notice(request.DraftNotice));
            }

            body.Append(Title(request.Title));

            if (!string.IsNullOrWhiteSpace(request.Reference))
            {
                body.Append(Text(request.Reference));
            }

            foreach (var line in (request.Body ?? string.Empty).Split('\n'))
            {
                body.Append(Text(line));
            }

            main.Document = new Document(body);
            main.Document.Save();

            // Гриф — В МЕТАДАННЫХ файла.
            document.PackageProperties.Title = request.Title;
            document.PackageProperties.Category = request.ClassificationMarking;
            document.PackageProperties.Keywords = request.ClassificationMarking;
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
