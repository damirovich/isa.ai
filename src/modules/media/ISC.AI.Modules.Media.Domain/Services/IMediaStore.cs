using ISC.AI.Modules.Media.Domain.Model;

namespace ISC.AI.Modules.Media.Domain.Services;

/// <summary>
/// Порт хранилища носителей (ТС-010, ТП-004/005): приём файла с дедупликацией по SHA-256 и
/// атомарная запись результата индексации (кадры, лица, шаблоны). Реализация — <c>Media.Data</c>.
/// </summary>
/// <remarks>
/// Байты носителя уходят в <c>IFileStorage</c> ядра (ADR-0018), в БД — только метаданные и хеш.
/// Гриф/подразделение носителя ДЕНОРМАЛИЗУЮТСЯ на каждое лицо и шаблон при записи (ТБ-020):
/// строка с вектором самодостаточна для фильтра, джойн через носитель не нужен.
/// </remarks>
public interface IMediaStore
{
    /// <summary>
    /// Принимает носитель: считает хеш, ищет дубликат (тот же хеш в том же подразделении), при его
    /// отсутствии сохраняет файл и создаёт запись со статусом <see cref="MediaIndexStatus.Uploaded"/>.
    /// </summary>
    Task<MediaAssetReceipt> ReceiveAsync(
        MediaAssetDraft draft, Stream content, CancellationToken cancellationToken = default);

    /// <summary>
    /// Метаданные носителя для фоновой индексации — БЕЗ контекста доступа: конвейер выполняется от имени
    /// системы, гриф/подразделение берутся у носителя и переносятся на производные (ТБ-070).
    /// <see langword="null"/> — носителя нет.
    /// </summary>
    Task<MediaAssetIndexingInfo?> GetForIndexingAsync(int assetId, CancellationToken cancellationToken = default);

    /// <summary>Переводит носитель в <see cref="MediaIndexStatus.Processing"/> (идемпотентно).</summary>
    Task MarkProcessingAsync(int assetId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Записывает результат конвейера ОДНОЙ транзакцией: кадры, лица, шаблоны, версии моделей,
    /// длительность (видео) и статус <see cref="MediaIndexStatus.Indexed"/>. Прежние лица/шаблоны
    /// носителя (переиндексация) снимаются в той же транзакции — половинчатого состояния нет.
    /// </summary>
    Task CompleteIndexingAsync(
        int assetId,
        IReadOnlyList<IndexedFace> faces,
        string detectorVersion,
        string embedderVersion,
        long? durationMs = null,
        CancellationToken cancellationToken = default);

    /// <summary>Фиксирует неудачу индексации с причиной (статус <see cref="MediaIndexStatus.Failed"/>).</summary>
    Task FailIndexingAsync(int assetId, string reason, CancellationToken cancellationToken = default);
}
