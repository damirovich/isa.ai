namespace ISC.AI.Abstractions.Ingestion;

/// <summary>
/// Загрузка документа из файла в корпус: извлечение текста (по формату) → <see cref="IIngestionPort"/>.
/// Нейтрально к типу документа; режимные инварианты (fail-closed гриф, дедуп) обеспечивает порт.
/// </summary>
public interface IFileIngestor
{
    /// <summary>Извлекает текст из файла и загружает документ в корпус через <see cref="IIngestionPort"/>.</summary>
    Task<IngestionResult> IngestFileAsync(FileIngestionRequest request, CancellationToken cancellationToken = default);
}
