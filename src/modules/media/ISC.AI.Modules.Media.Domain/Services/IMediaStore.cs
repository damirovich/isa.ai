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
    /// отсутствии сохраняет файл и создаёт запись со статусом <see cref="MediaIndexStatus.Uploaded"/>. Расширение
    /// имени в хранилище — только безопасное (<see cref="MediaFileNames.SafeExtension"/>), иначе <c>.bin</c>.
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

    /// <summary>
    /// Переводит носитель вида <see cref="MediaKind.Video"/>, в файле которого не оказалось видеопотока (голосовое
    /// <c>.3gp</c>, звук в mp4/webm), в <see cref="MediaKind.Audio"/> со статусом лиц
    /// <see cref="MediaIndexStatus.NotApplicable"/> (ADR-0026). Расшифровка не трогается.
    /// </summary>
    /// <remarks>
    /// Журнал аудита НЕ пишется: биометрия не обрабатывалась, и запись «индексация» утверждала бы обратное
    /// (ADR-0026 п.6). Вид носителя определён браузером по типу файла, а не содержимым, — это исправление
    /// метаданных, а не новое событие с материалом.
    /// </remarks>
    /// <returns><see langword="true"/> — носитель переведён; <see langword="false"/> — носителя нет или он уже не видео.</returns>
    Task<bool> ReclassifyAsAudioAsync(int assetId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Восстановление при старте хоста: очередь фоновых задач живёт в памяти и при перезапуске теряется, поэтому
    /// носители, оставшиеся «в очереди» или «в работе», навсегда висели бы в этом статусе. Одним обновлением на
    /// конвейер: расшифровка <see cref="TranscriptStatus.Pending"/>/<see cref="TranscriptStatus.Processing"/> и
    /// индексация лиц <see cref="MediaIndexStatus.Processing"/> → «ошибка» с причиной «прервано перезапуском»
    /// (повтор — вручную, как после любой ошибки).
    /// </summary>
    /// <remarks>
    /// Индексация в <see cref="MediaIndexStatus.Uploaded"/> НЕ трогается: этот статус остаётся и после отказа
    /// конвейера по закрытому делу (ТБ-074, ADR-0024), и «ошибка» выдавала бы регламентный отказ за сбой.
    /// Вызывать только до запуска очереди — иначе свежая постановка была бы принята за прерванную.
    /// </remarks>
    Task<InterruptedWorkRecovery> RecoverInterruptedAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Метаданные носителя для фоновой расшифровки (ADR-0026) — без контекста доступа, как и для
    /// индексации: гриф и подразделение берутся у носителя и переносятся на фрагменты.
    /// <see langword="null"/> — носителя нет.
    /// </summary>
    Task<MediaAssetTranscriptionInfo?> GetForTranscriptionAsync(int assetId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Ставит расшифровку в <see cref="TranscriptStatus.Pending"/> для повторного запуска — УСЛОВНО, одним
    /// обновлением в БД: только если она сейчас не <see cref="TranscriptStatus.Pending"/> и не
    /// <see cref="TranscriptStatus.Processing"/>. Два нажатия «Расшифровать заново» (в том числе параллельных)
    /// не ставят в общую последовательную очередь два полных прогона одной записи.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> — статус переведён, задачу можно ставить; <see langword="false"/> — расшифровка уже
    /// в очереди или выполняется (либо носителя нет), новая задача не нужна.
    /// </returns>
    Task<bool> TryMarkTranscriptionPendingAsync(int assetId, CancellationToken cancellationToken = default);

    /// <summary>Переводит расшифровку в <see cref="TranscriptStatus.Processing"/>.</summary>
    Task MarkTranscriptionProcessingAsync(int assetId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Записывает расшифровку ОДНОЙ транзакцией: прежние фрагменты носителя снимаются, новые — с грифом и
    /// подразделением носителя; версия модели, время, статус <see cref="TranscriptStatus.Done"/>.
    /// <paramref name="durationMs"/> — длительность ЗАПИСИ (длина подготовленного звука), которую сообщил
    /// распознаватель, а не конец последней речи. У аудио она записывается всегда, когда известна, — поверх
    /// прежнего значения: другого измерителя у аудио нет, и повторная расшифровка исправляет старую оценку. У видео
    /// длительность даёт раскадровка, и значение распознавателя записывается, только если её ещё нет.
    /// </summary>
    Task CompleteTranscriptionAsync(
        int assetId,
        IReadOnlyList<TranscriptSegmentDraft> segments,
        string modelVersion,
        long? durationMs = null,
        CancellationToken cancellationToken = default);

    /// <summary>Фиксирует неудачу расшифровки с причиной (статус <see cref="TranscriptStatus.Failed"/>).</summary>
    Task FailTranscriptionAsync(int assetId, string reason, CancellationToken cancellationToken = default);
}
