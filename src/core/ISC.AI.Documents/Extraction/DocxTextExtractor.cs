using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using ISC.AI.Abstractions.Documents;

namespace ISC.AI.Documents.Extraction;

/// <summary>
/// Извлекатель текста из .docx (OpenXml). Абзацы разделяются ПУСТОЙ СТРОКОЙ (<c>\n\n</c>) — именно её
/// чанкеры (<c>SimpleTextChunker</c>/<c>NpaStructuralChunker</c>) считают границей абзаца (ТО-мат-03).
/// Одинарный перевод строки они за границу не принимают, поэтому склейка через <c>\n</c> сделала бы весь
/// документ одним абзацем и привела бы к слепому резу посреди предложения.
/// </summary>
public sealed class DocxTextExtractor : IFormatTextExtractor
{
    /// <inheritdoc />
    public IReadOnlyCollection<string> Extensions { get; } = [".docx"];

    /// <inheritdoc />
    public async Task<ExtractedDocument> ExtractAsync(Stream content, CancellationToken cancellationToken = default)
    {
        // OpenXml требует перематываемый поток — копируем в память (файлы документов умеренного размера).
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        buffer.Position = 0;

        using var document = WordprocessingDocument.Open(buffer, isEditable: false);
        var body = document.MainDocumentPart?.Document?.Body;

        IEnumerable<string> paragraphs = body?
            .Descendants<Paragraph>()
            .Select(p => p.InnerText)
            .Where(t => !string.IsNullOrWhiteSpace(t)) ?? [];
        // Разделитель абзацев — ПУСТАЯ строка (\n\n): её распознают чанкеры как границу абзаца (ТО-мат-03).
        var text = string.Join("\n\n", paragraphs).Trim();

        var title = document.PackageProperties.Title;
        return new ExtractedDocument(text, string.IsNullOrWhiteSpace(title) ? null : title);
    }
}
