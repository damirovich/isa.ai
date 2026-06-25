namespace ISC.AI.Abstractions.Documents;

/// <summary>
/// Фасад извлечения текста: по имени файла выбирает подходящий <see cref="IFormatTextExtractor"/>.
/// Нейтрален к типу документа (ADR-0013) — работает с любым материалом корпуса.
/// </summary>
public interface ITextExtractor
{
    /// <summary>Есть ли извлекатель для данного файла (по расширению).</summary>
    bool CanExtract(string fileName);

    /// <summary>
    /// Извлекает текст из файла. Если формат не поддержан — <see cref="UnsupportedDocumentFormatException"/>
    /// (явный отказ, не молчаливый пропуск).
    /// </summary>
    Task<ExtractedDocument> ExtractAsync(Stream content, string fileName, CancellationToken cancellationToken = default);
}
