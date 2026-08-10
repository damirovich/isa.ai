using Microsoft.Extensions.Logging;

namespace ISC.AI.Modules.DocFlow.Data;

/// <summary>
/// Сообщения журнала индексатора (LoggerMessage — компаньон в отдельном файле, по общей конвенции).
/// Все три случая — штатный ПРОПУСК файла при индексации: текст карточки индексируется всегда,
/// а оператор по журналу видит, какие файлы в поиск не попали и почему.
/// </summary>
internal static partial class DocFlowDocumentIndexerLog
{
    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Индексация: файл «{FileName}» документа {DocumentId} пропущен — формат не поддержан извлечением текста.")]
    public static partial void FileFormatUnsupported(ILogger logger, string fileName, int documentId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Индексация: файл «{FileName}» документа {DocumentId} пропущен — текст пуст (вероятно, скан без текстового слоя).")]
    public static partial void FileTextEmpty(ILogger logger, string fileName, int documentId);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Индексация: файл «{FileName}» документа {DocumentId} пропущен — извлечение текста не удалось.")]
    public static partial void FileExtractionFailed(ILogger logger, Exception exception, string fileName, int documentId);
}
