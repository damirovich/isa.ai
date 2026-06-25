using ISC.AI.Abstractions.Documents;
using ISC.AI.Abstractions.Ingestion;

namespace ISC.AI.Ingestion;

/// <summary>
/// Загрузка из файла (ТО-мат-03, Э4-01): извлечь текст по формату → построить <see cref="IngestionRequest"/>
/// → передать в <see cref="IIngestionPort"/>. Сам порт обеспечивает fail-closed по грифу (ТБ-024),
/// дедуп и транзакционную запись — здесь не дублируем.
/// </summary>
public sealed class FileIngestionService(ITextExtractor extractor, IIngestionPort port) : IFileIngestor
{
    /// <inheritdoc />
    public async Task<IngestionResult> IngestFileAsync(FileIngestionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Формат не поддержан — явный отказ оператору (не молчаливый пропуск).
        if (!extractor.CanExtract(request.FileName))
        {
            return IngestionResult.Reject($"Формат файла не поддержан: «{request.FileName}».");
        }

        var extracted = await extractor.ExtractAsync(request.Content, request.FileName, cancellationToken);

        // Наименование: явное → из метаданных файла → имя файла без расширения.
        var title = !string.IsNullOrWhiteSpace(request.Title) ? request.Title
            : !string.IsNullOrWhiteSpace(extracted.Title) ? extracted.Title!
            : Path.GetFileNameWithoutExtension(request.FileName);

        var ingestionRequest = new IngestionRequest(
            DocType: request.DocType,
            Title: title,
            Text: extracted.Text,
            Classification: request.Classification,
            DivisionId: request.DivisionId,
            Source: request.Source ?? request.FileName,
            StorageUri: request.StorageUri,
            DocDate: request.DocDate,
            Metadata: request.Metadata);

        return await port.IngestAsync(ingestionRequest, cancellationToken);
    }
}
