using ISC.AI.Abstractions.Documents;
using ISC.AI.Documents;
using ISC.AI.Documents.Extraction;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace ISC.AI.UnitTests.Documents;

/// <summary>
/// OCR сканов (ПОДГ-02): извлекатель заявляет форматы-изображения, при ненастроенном OCR отказывает ЯВНО
/// (не «тихий мусор»), а фасад маршрутизирует скан именно в OCR-извлекатель. Само распознавание
/// (картинка→текст) требует офлайн-файлов traineddata и native-движка — проверяется на стенде оператора,
/// не в юнит-среде.
/// </summary>
public sealed class TesseractOcrTextExtractorTests
{
    [Fact(DisplayName = "ПОДГ-02: OCR-извлекатель заявляет форматы-изображения (сканы)")]
    public void Declares_image_formats()
    {
        using var extractor = new TesseractOcrTextExtractor(OcrOptions.NotConfigured);

        extractor.Extensions.ShouldContain(".png");
        extractor.Extensions.ShouldContain(".jpg");
        extractor.Extensions.ShouldContain(".tiff");
    }

    [Fact(DisplayName = "ПОДГ-02: OCR не настроен (нет tessdata) → явная ошибка, а не пустой результат")]
    public async Task Unconfigured_ocr_fails_explicitly()
    {
        using var extractor = new TesseractOcrTextExtractor(
            new OcrOptions(TessdataPath: "Z:\\нет-такого-каталога-tessdata", Languages: "rus"));
        using var content = new MemoryStream([1, 2, 3]); // содержимое не важно — отказ ДО распознавания

        var exception = await Should.ThrowAsync<InvalidOperationException>(() => extractor.ExtractAsync(content));
        exception.Message.ShouldContain("OCR не настроен");
    }

    [Fact(DisplayName = "ПОДГ-02: фасад маршрутизирует скан (.png) в OCR-извлекатель; .docx и .txt — как раньше")]
    public void Composite_routes_scan_image_to_ocr()
    {
        // OCR зарегистрирован даже без конфигурации: скан распознаётся как OCR-формат (при попытке распознать
        // без tessdata будет явная ошибка), а не отвергается как «формат не поддерживается».
        var configuration = new ConfigurationBuilder().Build();
        using var provider = new ServiceCollection().AddCoreDocuments(configuration).BuildServiceProvider();

        var extractor = provider.GetRequiredService<ITextExtractor>();

        extractor.CanExtract("приказ-скан.png").ShouldBeTrue();
        extractor.CanExtract("положение.docx").ShouldBeTrue();
        extractor.CanExtract("выгрузка.txt").ShouldBeTrue();
        extractor.CanExtract("архив.zip").ShouldBeFalse();
    }
}
