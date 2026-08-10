using ISC.AI.Abstractions.Documents;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace ISC.AI.Documents.Extraction;

/// <summary>
/// Извлекатель текста из PDF с ТЕКСТОВЫМ СЛОЕМ (PdfPig, Apache-2.0). Страницы разделяются пустой
/// строкой (<c>\n\n</c>) — границей абзаца для чанкеров (ТО-мат-03), как в <see cref="DocxTextExtractor"/>.
/// </summary>
/// <remarks>
/// СКАН (PDF без текстового слоя) даёт здесь пустой текст — извлекатель этого не скрывает и не
/// пытается распознавать сам: путь сканов — OCR (Tesseract), а решение «что делать с пустым
/// текстом» принимает вызывающий (конвейер загрузки отклонит явно, индексатор документооборота
/// пропустит файл, сохранив текст карточки). Текст читается в порядке следования
/// (<c>ContentOrderTextExtractor</c>), а не в порядке операторов файла: у PDF из вёрстки порядок
/// операторов может не совпадать с порядком чтения.
/// </remarks>
public sealed class PdfTextExtractor : IFormatTextExtractor
{
    /// <inheritdoc />
    public IReadOnlyCollection<string> Extensions { get; } = [".pdf"];

    /// <inheritdoc />
    public async Task<ExtractedDocument> ExtractAsync(Stream content, CancellationToken cancellationToken = default)
    {
        // PdfPig требует произвольный доступ — копируем в память (файлы документов умеренного размера).
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);

        using var document = PdfDocument.Open(buffer.ToArray());

        var pages = new List<string>(document.NumberOfPages);
        foreach (var page in document.GetPages())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pageText = ContentOrderTextExtractor.GetText(page).Trim();
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
}
