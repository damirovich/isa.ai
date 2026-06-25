namespace ISC.AI.Abstractions.Documents;

/// <summary>
/// Извлекатель текста для КОНКРЕТНОГО формата файла (.docx, .txt, скан/изображение через OCR и т. д.).
/// Нейтрален к типу документа: извлекает текст, не зная семантики материала.
/// </summary>
public interface IFormatTextExtractor
{
    /// <summary>Поддерживаемые расширения файла — в нижнем регистре, с точкой (например, «.docx»).</summary>
    IReadOnlyCollection<string> Extensions { get; }

    /// <summary>Извлекает текст из потока содержимого файла.</summary>
    Task<ExtractedDocument> ExtractAsync(Stream content, CancellationToken cancellationToken = default);
}
