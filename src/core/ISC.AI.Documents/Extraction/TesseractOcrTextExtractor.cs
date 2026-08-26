using ISC.AI.Abstractions.Documents;
using Tesseract;

namespace ISC.AI.Documents.Extraction;

/// <summary>
/// OCR-извлекатель текста из сканов/изображений через Tesseract (ПОДГ-02). Обрабатывает форматы-изображения
/// (сканы приказов и т. п.). Языки и каталог <c>tessdata</c> — из <see cref="OcrOptions"/> (офлайн-ассеты
/// оператора, изолированный контур).
/// </summary>
/// <remarks>
/// Движок <see cref="TesseractEngine"/> НЕ потокобезопасен и дорог в создании: создаётся ЛЕНИВО один раз,
/// доступ к <see cref="TesseractEngine.Process(Pix)"/> сериализуется семафором. Если OCR не настроен или
/// файлы <c>{язык}.traineddata</c>/native-библиотеки отсутствуют — ЯВНАЯ ошибка (не молчаливый пустой
/// результат, ср. принцип «явный отказ» в конвейере загрузки). PDF-сканы сюда не попадают как файл:
/// их страницы-изображения достаёт <see cref="PdfTextExtractor"/> и распознаёт через <see cref="IImageOcr"/>
/// этого же класса (один движок и одна очередь на процесс).
/// </remarks>
public sealed class TesseractOcrTextExtractor(OcrOptions options) : IFormatTextExtractor, IImageOcr, IDisposable
{
    /// <inheritdoc />
    public IReadOnlyCollection<string> Extensions { get; } = [".png", ".jpg", ".jpeg", ".tif", ".tiff", ".bmp"];

    // TesseractEngine не потокобезопасен: сериализуем распознавание (в т.ч. ленивую инициализацию движка).
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TesseractEngine? _engine;
    private bool _disposed;

    /// <inheritdoc />
    public async Task<ExtractedDocument> ExtractAsync(Stream content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        // Tesseract читает из памяти — копируем поток целиком (файлы сканов умеренного размера).
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);

        var text = await RecognizeAsync(buffer.ToArray(), cancellationToken);
        return new ExtractedDocument(text, Title: null);
    }

    /// <inheritdoc />
    public async Task<string> RecognizeAsync(byte[] imageBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(imageBytes);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var engine = GetOrCreateEngine();
            using var image = Pix.LoadFromMemory(imageBytes);
            using var page = engine.Process(image);
            return (page.GetText() ?? string.Empty).Trim();
        }
        finally
        {
            _gate.Release();
        }
    }

    // Ленивая инициализация под _gate (доступ сериализован) — без доп. блокировки. Явный отказ вместо
    // молчаливого пустого результата, если OCR не настроен или traineddata/native-либы отсутствуют.
    private TesseractEngine GetOrCreateEngine()
    {
        if (_engine is not null)
        {
            return _engine;
        }

        if (!options.IsConfigured || !Directory.Exists(options.TessdataPath))
        {
            throw new InvalidOperationException(
                $"OCR не настроен: каталог tessdata не задан или не существует (Ocr:TessdataPath = «{options.TessdataPath}»). " +
                "В изолированном контуре файлы {язык}.traineddata (например, rus, kir) предоставляются оператором офлайн.");
        }

        try
        {
            _engine = new TesseractEngine(options.TessdataPath, options.Languages, EngineMode.Default);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"Не удалось инициализировать OCR (языки «{options.Languages}», каталог «{options.TessdataPath}»): " +
                "проверьте наличие файлов {язык}.traineddata для каждого языка и native-библиотек Tesseract.",
                exception);
        }

        return _engine;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _engine?.Dispose();
        _gate.Dispose();
        _disposed = true;
    }
}
