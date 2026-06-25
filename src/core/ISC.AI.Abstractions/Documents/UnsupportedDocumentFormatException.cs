namespace ISC.AI.Abstractions.Documents;

/// <summary>
/// Формат файла не поддержан ни одним извлекателем текста. Бросается явно, чтобы оператор видел
/// причину отказа, а не получал «пустую» загрузку.
/// </summary>
public sealed class UnsupportedDocumentFormatException(string fileName)
    : Exception($"Нет извлекателя текста для файла «{fileName}» (неподдержанный формат).")
{
    /// <summary>Имя файла, формат которого не поддержан.</summary>
    public string FileName { get; } = fileName;
}
