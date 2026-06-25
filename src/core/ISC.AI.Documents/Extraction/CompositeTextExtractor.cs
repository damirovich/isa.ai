using ISC.AI.Abstractions.Documents;

namespace ISC.AI.Documents.Extraction;

/// <summary>
/// Фасад извлечения: выбирает <see cref="IFormatTextExtractor"/> по расширению файла. Если подходящего
/// нет — <see cref="UnsupportedDocumentFormatException"/> (явный отказ, не молчаливый пропуск).
/// </summary>
public sealed class CompositeTextExtractor(IEnumerable<IFormatTextExtractor> extractors) : ITextExtractor
{
    private readonly IReadOnlyList<IFormatTextExtractor> _extractors = [.. extractors];

    /// <inheritdoc />
    public bool CanExtract(string fileName) => Resolve(fileName) is not null;

    /// <inheritdoc />
    public Task<ExtractedDocument> ExtractAsync(Stream content, string fileName, CancellationToken cancellationToken = default)
    {
        var extractor = Resolve(fileName) ?? throw new UnsupportedDocumentFormatException(fileName);
        return extractor.ExtractAsync(content, cancellationToken);
    }

    private IFormatTextExtractor? Resolve(string fileName)
    {
        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        return string.IsNullOrEmpty(extension)
            ? null
            : _extractors.FirstOrDefault(e => e.Extensions.Contains(extension));
    }
}
