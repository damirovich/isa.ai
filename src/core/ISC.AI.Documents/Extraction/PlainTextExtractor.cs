using ISC.AI.Abstractions.Documents;

namespace ISC.AI.Documents.Extraction;

/// <summary>Извлекатель простого текста (.txt). Кодировка — по BOM, по умолчанию UTF-8.</summary>
public sealed class PlainTextExtractor : IFormatTextExtractor
{
    /// <inheritdoc />
    public IReadOnlyCollection<string> Extensions { get; } = [".txt"];

    /// <inheritdoc />
    public async Task<ExtractedDocument> ExtractAsync(Stream content, CancellationToken cancellationToken = default)
    {
        using var reader = new StreamReader(content, leaveOpen: true);
        var text = await reader.ReadToEndAsync(cancellationToken);
        return new ExtractedDocument(text.Trim());
    }
}
