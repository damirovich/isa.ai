namespace ISC.AI.Modules.Media.Application.Features.Import;

/// <summary>
/// Форматы документов архива (ADR-0042): <c>.docx</c>, <c>.doc</c>, <c>.pdf</c> — те, что принимает документооборот, — и
/// сверка содержимого с расширением по сигнатуре, как у носителей (ТС-010): переименованный файл чужого формата не
/// попадёт в документооборот под видом документа.
/// </summary>
public static class ArchiveDocumentFormats
{
    /// <summary>MIME-тип <c>.docx</c>.</summary>
    public const string DocxContentType = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    /// <summary>MIME-тип <c>.doc</c>.</summary>
    public const string DocContentType = "application/msword";

    /// <summary>MIME-тип <c>.pdf</c>.</summary>
    public const string PdfContentType = "application/pdf";

    /// <summary>Отказ: содержимое не соответствует расширению.</summary>
    public const string SignatureMismatchMessage = "содержимое файла не соответствует расширению документа";

    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".docx"] = DocxContentType,
        [".doc"] = DocContentType,
        [".pdf"] = PdfContentType,
    };

    // Сигнатура составного файла OLE (Word 97–2003).
    private static ReadOnlySpan<byte> OleSignature => [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    /// <summary>Расширения документов архива.</summary>
    public static IReadOnlyCollection<string> Extensions => ByExtension.Keys;

    /// <summary>MIME-тип документа по имени файла или <see langword="null"/>, если это не документ архива.</summary>
    public static string? ContentTypeForFile(string? fileName)
    {
        var extension = Path.GetExtension(fileName ?? string.Empty);
        return extension.Length > 0 && ByExtension.TryGetValue(extension, out var type) ? type : null;
    }

    /// <summary>
    /// Содержимое соответствует типу: <c>.docx</c> — архив ZIP (<c>PK</c>), <c>.doc</c> — составной файл OLE или RTF
    /// (так сохраняли многие учётные программы), <c>.pdf</c> — <c>%PDF</c> в начале.
    /// </summary>
    public static bool MatchesSignature(string contentType, ReadOnlySpan<byte> head) => contentType switch
    {
        DocxContentType => head.StartsWith("PK\u0003\u0004"u8),
        DocContentType => head.StartsWith(OleSignature) || head.StartsWith("{\\rtf"u8),
        PdfContentType => head.StartsWith("%PDF"u8),
        _ => false,
    };
}
