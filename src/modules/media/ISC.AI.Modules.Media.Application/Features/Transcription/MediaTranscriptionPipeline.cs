using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Storage;
using ISC.AI.Modules.Media.Application.Features.Maintenance;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using Microsoft.Extensions.Logging;

namespace ISC.AI.Modules.Media.Application.Features.Transcription;

/// <summary>
/// Конвейер расшифровки речи носителя (ADR-0026): исходник из хранилища → временная копия → распознаватель
/// (<see cref="IAudioTranscriber"/>: ffmpeg → детектор речи → модель во внешнем процессе) → атомарная запись
/// фрагментов с грифом носителя. Исполняется фоновой очередью ядра из per-operation scope; повторный запуск
/// идемпотентен — расшифровка носителя перезаписывается хранилищем целиком одной транзакцией.
/// </summary>
/// <remarks>
/// <para>
/// Ошибка любого шага НЕ пробрасывается: фиксируется в статусе носителя
/// (<see cref="IMediaStore.FailTranscriptionAsync"/>) и возвращается в <see cref="MediaTranscriptionResult"/> —
/// фоновой задаче незачем падать, оператор видит причину в карточке. Прежняя расшифровка при этом остаётся
/// (её заменяет только успешный прогон). ОТМЕНА (остановка приложения, снятие задачи) — фиксируется как
/// «расшифровка отменена» и пробрасывается, чтобы воркер остановился штатно (как в <c>MediaIndexer</c>).
/// Отменой считается только срабатывание СВОЕГО токена: <see cref="OperationCanceledException"/> изнутри
/// распознавателя или его зависимостей без отмены задачи — обычный сбой с причиной. (Таймаут внешнего процесса
/// адаптер сам сообщает как <see cref="InvalidOperationException"/> с понятной причиной, настройка
/// <c>Speech:TimeoutFactor</c>; это правило — страховка на случай иной реализации порта.)
/// </para>
/// <para>
/// АУДИТ (ТБ-030): расшифровка — порождение нового материала моделью, событие журнала
/// <see cref="AuditAction.Ingest"/> <c>media:asset:{id}:transcribed</c> с грифом и подразделением НОСИТЕЛЯ,
/// без субъекта (конвейер работает от имени системы); в чувствительной части — модель и число фрагментов.
/// Расшифровка автоматическая и требует проверки человеком (ТЭ-002): это сказано и в записи журнала.
/// Журнал недоступен после фиксации фрагментов — носитель помечается «ошибка» с причиной (как у индексации
/// лиц): оператор видит, что прогон не завершён штатно, и повторяет его.
/// </para>
/// </remarks>
public sealed class MediaTranscriptionPipeline(
    IMediaStore store,
    IFileStorage fileStorage,
    IAudioTranscriber transcriber,
    IAuditWriter auditWriter,
    MediaTempFiles tempFiles,
    ILogger<MediaTranscriptionPipeline> logger) : IMediaTranscriptionPipeline
{
    /// <summary>Причина отказа для изображения (результат задачи; статус носителя не меняется).</summary>
    public const string NotApplicableToImageError = "К изображению расшифровка неприменима.";

    /// <summary>Причина, записываемая носителю при отмене расшифровки.</summary>
    public const string CancelledReason = "расшифровка отменена";

    /// <inheritdoc />
    public async Task<MediaTranscriptionResult> TranscribeAsync(int assetId, CancellationToken cancellationToken = default)
    {
        var info = await store.GetForTranscriptionAsync(assetId, cancellationToken);
        if (info is null)
        {
            MediaTranscriptionLog.AssetNotFound(logger, assetId);
            return new MediaTranscriptionResult(false, 0, "Носитель не найден.");
        }

        // В изображении речи нет: ни статуса, ни журнала — задача пришла по ошибке и ничего не делает.
        if (info.Kind == MediaKind.Image)
        {
            MediaTranscriptionLog.NotApplicableToImage(logger, assetId);
            return new MediaTranscriptionResult(false, 0, NotApplicableToImageError);
        }

        var segments = new List<TranscriptSegmentDraft>();
        try
        {
            await store.MarkTranscriptionProcessingAsync(assetId, cancellationToken);

            // Длительность ЗАПИСИ (длина подготовленного звука) сообщает распознаватель — не конец последней
            // речи: у диктофона на 45 минут с последней фразой на 12-й минуте длительность — 45 минут.
            var durationMs = await TranscribeSourceAsync(info, segments, cancellationToken);
            var modelVersion = transcriber.ModelVersion;

            await store.CompleteTranscriptionAsync(assetId, segments, modelVersion, durationMs, cancellationToken);

            // ТБ-030: новый материал, порождённый моделью, — в неизменяемый журнал с режимом носителя.
            await auditWriter.WriteAsync(
                new AuditEntry(
                    AuditAction.Ingest,
                    info.Classification,
                    SubjectId: null,
                    ObjectRef: "media:asset:" + assetId.ToString(CultureInfo.InvariantCulture) + ":transcribed",
                    DivisionId: info.DivisionId,
                    PayloadSensitive:
                        $"расшифровка речи: фрагментов {segments.Count}, модель {modelVersion}; "
                        + "автоматическая расшифровка, требует проверки, не является протоколом (ТЭ-002, ADR-0026)"),
                cancellationToken);

            MediaTranscriptionLog.Completed(logger, assetId, segments.Count, modelVersion);
            return new MediaTranscriptionResult(true, segments.Count);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Носитель не должен зависнуть в «выполняется»: фиксируем «отменено» токеном, который уже не
            // отменён (иначе запись не дойдёт), и пробрасываем — воркер останавливается штатно.
            MediaTranscriptionLog.Cancelled(logger, assetId, segments.Count);
            await store.FailTranscriptionAsync(assetId, CancelledReason, CancellationToken.None);
            throw;
        }
        catch (Exception exception)
        {
            MediaTranscriptionLog.Failed(logger, exception, assetId, segments.Count);
            await store.FailTranscriptionAsync(assetId, exception.Message, CancellationToken.None);
            return new MediaTranscriptionResult(false, segments.Count, exception.Message);
        }
    }

    /// <summary>
    /// Копирует исходник во временный файл (распознаватель — внешний процесс — работает с путём, а не потоком)
    /// и собирает фрагменты. Пустые фрагменты (детектор услышал звук, модель слов не нашла) не сохраняются:
    /// в них нет ни текста для чтения, ни слов для поиска. Текст непустых — ДОСЛОВНО, без правок. Временный
    /// файл удаляется в любом исходе. Возвращает длительность записи от распознавателя.
    /// </summary>
    private async Task<long?> TranscribeSourceAsync(
        MediaAssetTranscriptionInfo info, List<TranscriptSegmentDraft> segments, CancellationToken cancellationToken)
    {
        // Копия — в управляемом каталоге (ТБ-064): переживи она аварийную остановку, её удалит уборка при старте
        // хоста. Расширение — через фильтр: путь уходит ffmpeg/ffprobe текстом командной строки, а носители,
        // сохранённые до фильтра при приёме, могут нести в имени кавычку или «\».
        var tempPath = tempFiles.NewPath(MediaTempFiles.SpeechPrefix, info.StoredFileName);
        try
        {
            await using (var source = await fileStorage.OpenReadAsync(
                info.StoredFileName,
                MediaFileCategories.Originals,
                info.AssetId.ToString(CultureInfo.InvariantCulture),
                cancellationToken))
            await using (var target = File.Create(tempPath))
            {
                await source.CopyToAsync(target, cancellationToken);
            }

            var transcription = await transcriber.TranscribeAsync(tempPath, cancellationToken);
            foreach (var segment in transcription.Segments)
            {
                if (!string.IsNullOrWhiteSpace(segment?.Text))
                {
                    segments.Add(segment);
                }
            }

            return transcription.DurationMs;
        }
        finally
        {
            try
            {
                File.Delete(tempPath);
            }
            catch (IOException exception)
            {
                MediaTranscriptionLog.TempFileNotDeleted(logger, exception, info.AssetId, tempPath);
            }
            catch (UnauthorizedAccessException exception)
            {
                MediaTranscriptionLog.TempFileNotDeleted(logger, exception, info.AssetId, tempPath);
            }
        }
    }
}
