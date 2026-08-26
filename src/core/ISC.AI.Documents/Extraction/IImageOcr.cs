namespace ISC.AI.Documents.Extraction;

/// <summary>
/// Распознавание текста на ОДНОМ изображении (OCR, ПОДГ-02). Внутренний контракт слоя документов:
/// им пользуется <see cref="PdfTextExtractor"/> для страниц-сканов внутри PDF; реализация —
/// <see cref="TesseractOcrTextExtractor"/> (та же, что распознаёт файлы-изображения целиком).
/// </summary>
public interface IImageOcr
{
    /// <summary>
    /// Распознаёт текст на изображении (PNG/JPEG/TIFF/BMP в байтах). Если OCR не настроен
    /// (нет каталога tessdata) — явная ошибка, не молчаливый пустой результат.
    /// </summary>
    /// <param name="imageBytes">Содержимое файла изображения.</param>
    /// <param name="cancellationToken">Отмена длительного распознавания.</param>
    /// <returns>Распознанный текст (может быть пустым, если на изображении текста нет).</returns>
    Task<string> RecognizeAsync(byte[] imageBytes, CancellationToken cancellationToken = default);
}
