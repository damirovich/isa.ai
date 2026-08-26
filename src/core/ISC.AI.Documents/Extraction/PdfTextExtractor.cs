using ISC.AI.Abstractions.Documents;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace ISC.AI.Documents.Extraction;

/// <summary>
/// Извлекатель текста из PDF (PdfPig, Apache-2.0). Страница с ТЕКСТОВЫМ СЛОЕМ читается напрямую;
/// страница-СКАН (без текстового слоя — типичный вывод сканера) распознаётся через OCR
/// (<see cref="IImageOcr"/>, ПОДГ-02): из страницы достаются вложенные изображения и прогоняются
/// через Tesseract. Страницы разделяются пустой строкой (<c>\n\n</c>) — границей абзаца для
/// чанкеров (ТО-мат-03), как в <see cref="DocxTextExtractor"/>.
/// </summary>
/// <remarks>
/// Смешанные документы (часть страниц с текстом, часть — сканы) поддерживаются: каждая страница
/// идёт своим путём в порядке следования. Если OCR не настроен (нет tessdata), PDF с текстовым
/// слоем работает как раньше, а первая же страница-скан даёт ЯВНУЮ ошибку «OCR не настроен» —
/// не молчаливый пропуск содержимого (принцип «явный отказ» конвейера загрузки). Растеризация
/// страниц не выполняется: у сканов страница и есть изображение, его и распознаём; PDF без текста
/// И без извлекаемых изображений даст пустой текст — решение об отклонении принимает вызывающий.
/// Текст читается в порядке следования (<c>ContentOrderTextExtractor</c>), а не в порядке
/// операторов файла: у PDF из вёрстки порядок операторов может не совпадать с порядком чтения.
/// </remarks>
public sealed class PdfTextExtractor(IImageOcr imageOcr) : IFormatTextExtractor
{
    /// <inheritdoc />
    public IReadOnlyCollection<string> Extensions { get; } = [".pdf"];

    /// <inheritdoc />
    public async Task<ExtractedDocument> ExtractAsync(Stream content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);

        // PdfPig требует произвольный доступ — копируем в память (файлы документов умеренного размера).
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);

        using var document = PdfDocument.Open(buffer.ToArray());

        var pages = new List<string>(document.NumberOfPages);
        foreach (var page in document.GetPages())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var pageText = ContentOrderTextExtractor.GetText(page).Trim();
            if (pageText.Length == 0)
            {
                // Нет текстового слоя — страница-скан: распознаём её изображения (ПОДГ-02).
                pageText = await RecognizePageImagesAsync(page, cancellationToken);
            }

            if (pageText.Length > 0)
            {
                pages.Add(pageText);
            }
        }

        var title = document.Information.Title;
        return new ExtractedDocument(
            string.Join("\n\n", pages).Trim(),
            string.IsNullOrWhiteSpace(title) ? null : title);
    }

    // OCR всех изображений страницы в порядке следования. PdfPig отдаёт изображение либо как PNG
    // (декодируемые фильтры: Flate/CCITT и т.п.), либо как исходные байты (DCTDecode = готовый JPEG,
    // Tesseract читает его напрямую). Ошибка распознавания НЕ глотается: для страницы-скана её
    // изображение — всё содержимое страницы.
    private async Task<string> RecognizePageImagesAsync(Page page, CancellationToken cancellationToken)
    {
        var fragments = new List<string>();
        foreach (var image in page.GetImages())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var imageBytes = image.TryGetPng(out var png) ? png : image.RawBytes.ToArray();
            var recognized = await imageOcr.RecognizeAsync(imageBytes, cancellationToken);
            if (recognized.Length > 0)
            {
                fragments.Add(recognized);
            }
        }

        return string.Join("\n", fragments).Trim();
    }
}
