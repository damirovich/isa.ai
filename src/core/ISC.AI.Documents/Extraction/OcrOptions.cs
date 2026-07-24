namespace ISC.AI.Documents.Extraction;

/// <summary>
/// Настройки OCR (ПОДГ-02). В ИЗОЛИРОВАННОМ контуре файлы <c>{язык}.traineddata</c> предоставляются
/// оператором ОФЛАЙН (интернета нет), а путь к каталогу и языки задаются конфигурацией — движок не
/// скачивает модели и не имеет небезопасных значений по умолчанию.
/// </summary>
/// <param name="TessdataPath">
/// Каталог с файлами <c>{язык}.traineddata</c>. <see langword="null"/>/пусто — OCR НЕ настроен: попытка
/// распознать скан завершается явной ошибкой (не «тихим мусором»).
/// </param>
/// <param name="Languages">
/// Языки Tesseract через <c>+</c> (например, <c>«rus+kir»</c> — русский и киргизский). Для каждого нужен
/// свой файл <c>{язык}.traineddata</c> в <paramref name="TessdataPath"/>.
/// </param>
public sealed record OcrOptions(string? TessdataPath, string Languages = "rus+kir")
{
    /// <summary>OCR не настроен (нет каталога traineddata) — сканы распознать нельзя, но .docx/.txt работают.</summary>
    public static readonly OcrOptions NotConfigured = new(TessdataPath: null);

    /// <summary>Настроен ли OCR (задан существующий каталог traineddata).</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(TessdataPath);
}
