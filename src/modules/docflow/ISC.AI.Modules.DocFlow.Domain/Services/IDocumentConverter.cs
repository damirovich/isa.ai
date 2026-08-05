namespace ISC.AI.Modules.DocFlow.Domain.Services;

/// <summary>
/// Конвертация DOCX в PDF для просмотра без скачивания (§3.3). ЛУЧШЕЕ УСИЛИЕ (best-effort) НАМЕРЕННО:
/// внешний процесс (LibreOffice) — не всегда доступен (не установлен в dev-окружении, не найден на
/// хосте), и это НЕ должно останавливать загрузку файла — документ с оригиналом ценнее документа без
/// PDF-превью. Отказ конвертации логируется и возвращает <see langword="null"/>, никогда не бросает
/// наружу вызывающему коду.
/// </summary>
public interface IDocumentConverter
{
    /// <summary>
    /// Конвертирует файл хранилища в PDF; возвращает имя PDF-копии в том же хранилище (категория/подпуть)
    /// либо <see langword="null"/>, если конвертер недоступен или конвертация не удалась.
    /// </summary>
    Task<string?> ConvertToPdfAsync(
        string storedFileName, string category, string subPath, CancellationToken cancellationToken = default);
}
