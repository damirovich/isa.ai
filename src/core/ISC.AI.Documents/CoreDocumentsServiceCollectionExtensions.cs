using ISC.AI.Abstractions.Documents;
using ISC.AI.Documents.Export;
using ISC.AI.Documents.Extraction;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ISC.AI.Documents;

/// <summary>Регистрация движка документов ядра: извлечение текста из файлов (нейтрально к типу документа).</summary>
public static class CoreDocumentsServiceCollectionExtensions
{
    /// <summary>Регистрирует извлекатели текста (.txt, .docx, .pdf, OCR сканов) и фасад <see cref="ITextExtractor"/>.</summary>
    public static IServiceCollection AddCoreDocuments(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IFormatTextExtractor, PlainTextExtractor>();
        services.AddSingleton<IFormatTextExtractor, DocxTextExtractor>();
        services.AddSingleton<IFormatTextExtractor, PdfTextExtractor>();

        // OCR сканов (ПОДГ-02) регистрируется ВСЕГДА: сканы/изображения распознаются как OCR-формат, а при
        // ненастроенном OCR попытка даёт ЯВНУЮ ошибку «OCR не настроен» (а не «формат не поддерживается»).
        // Путь к tessdata и языки — из конфигурации (офлайн-ассеты оператора, изолированный контур).
        services.AddSingleton(ReadOcrOptions(configuration));
        services.AddSingleton<IFormatTextExtractor, TesseractOcrTextExtractor>();

        services.TryAddSingleton<ITextExtractor, CompositeTextExtractor>();

        // Экспорт документов (.docx) с обязательной маркировкой грифа (Э4-04, ТБ-033).
        services.AddSingleton<IDocumentExporter, DocxDocumentExporter>();
        return services;
    }

    // Настройки OCR из секции Ocr. Путь не задан → OCR не настроен (сканы → явная ошибка при попытке).
    private static OcrOptions ReadOcrOptions(IConfiguration configuration)
    {
        var tessdataPath = configuration["Ocr:TessdataPath"];
        if (string.IsNullOrWhiteSpace(tessdataPath))
        {
            return OcrOptions.NotConfigured;
        }

        var languages = configuration["Ocr:Languages"];
        return new OcrOptions(tessdataPath, string.IsNullOrWhiteSpace(languages) ? "rus+kir" : languages);
    }
}
