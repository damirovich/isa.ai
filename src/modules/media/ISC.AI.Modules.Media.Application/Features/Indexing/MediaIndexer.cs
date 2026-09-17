using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Storage;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using Microsoft.Extensions.Logging;

namespace ISC.AI.Modules.Media.Application.Features.Indexing;

/// <summary>
/// Конвейер индексации носителя (ТП-005, ТО-мат-05): исходник из хранилища → (для видео) раскадровка
/// (ТО-мат-06) → детекция лиц → оценка качества (ТО-мат-07) → векторизация пригодных → вырезки →
/// атомарная запись шаблонов с грифом носителя (ТБ-070). Исполняется фоновой очередью ядра из
/// per-operation scope; повторный запуск идемпотентен — прежние вырезки снимаются, шаблоны
/// перезаписываются хранилищем одной транзакцией.
/// </summary>
/// <remarks>
/// Ошибка любого шага НЕ пробрасывается: фиксируется в статусе носителя (<see cref="IMediaStore.FailIndexingAsync"/>)
/// и возвращается в <see cref="MediaIndexResult"/> — фоновой задаче незачем падать, оператор видит причину в
/// карточке (ТФ-МЕД-02). Отмена — пробрасывается (воркер останавливается штатно). Результат индексации —
/// аудируемое событие (ТО-инф-11, ТБ-030): запись <see cref="AuditAction.Ingest"/> с грифом носителя, без
/// субъекта (конвейер работает от имени системы).
/// </remarks>
public sealed class MediaIndexer(
    IMediaStore store,
    IFileStorage fileStorage,
    IFaceDetector detector,
    IFaceEmbedder embedder,
    IFaceQualityAssessor qualityAssessor,
    IImageTools imageTools,
    IFrameExtractor frameExtractor,
    IAuditWriter auditWriter,
    MediaSearchOptions options,
    ILogger<MediaIndexer> logger) : IMediaIndexer
{
    /// <inheritdoc />
    public async Task<MediaIndexResult> IndexAsync(int assetId, CancellationToken cancellationToken = default)
    {
        var info = await store.GetForIndexingAsync(assetId, cancellationToken);
        if (info is null)
        {
            MediaIndexerLog.AssetNotFound(logger, assetId);
            return new MediaIndexResult(false, 0, 0, 0, "Носитель не найден.");
        }

        var progress = new Progress();
        try
        {
            await store.MarkProcessingAsync(assetId, cancellationToken);
            var subPath = assetId.ToString(CultureInfo.InvariantCulture);

            // Переиндексация: вырезки прежнего прогона снимаем с диска до записи новых (строки снимет
            // CompleteIndexingAsync в своей транзакции; файл без строки безвреден, строка без файла — нет).
            foreach (var crop in info.ExistingCropFileNames)
            {
                await fileStorage.DeleteAsync(crop, MediaFileCategories.FaceCrops, subPath, cancellationToken);
            }

            long? durationMs = await ProcessSourceAsync(info, subPath, progress, cancellationToken);

            await store.CompleteIndexingAsync(
                assetId, progress.Faces, detector.ModelVersion, embedder.ModelVersion, durationMs, cancellationToken);

            // ТО-инф-11: факт индексации биометрии — в неизменяемый журнал с грифом/подразделением носителя.
            await auditWriter.WriteAsync(
                new AuditEntry(
                    AuditAction.Ingest,
                    info.Classification,
                    SubjectId: null,
                    ObjectRef: $"media:asset:{assetId}:indexed",
                    DivisionId: info.DivisionId,
                    PayloadSensitive:
                        $"кадров {progress.Frames}, лиц {progress.Faces.Count}, отклонено {progress.Rejected}, "
                        + $"детектор {detector.ModelVersion}, векторизатор {embedder.ModelVersion}"),
                cancellationToken);

            MediaIndexerLog.Completed(logger, assetId, progress.Frames, progress.Faces.Count, progress.Rejected);
            return new MediaIndexResult(true, progress.Frames, progress.Faces.Count, progress.Rejected);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            MediaIndexerLog.Failed(logger, exception, assetId, progress.Frames);
            await store.FailIndexingAsync(assetId, exception.Message, cancellationToken);
            return new MediaIndexResult(false, progress.Frames, progress.Faces.Count, progress.Rejected, exception.Message);
        }
    }

    /// <summary>
    /// Копирует исходник во временный файл (раскадровщик работает с путём, а не потоком) и прогоняет
    /// кадры через детектор/векторизатор. Возвращает длительность видео (последний таймкод) либо
    /// <see langword="null"/> для изображения. Временный файл удаляется в любом исходе.
    /// </summary>
    private async Task<long?> ProcessSourceAsync(
        MediaAssetIndexingInfo info, string subPath, Progress progress, CancellationToken cancellationToken)
    {
        var tempPath = Path.Combine(
            Path.GetTempPath(),
            "isc-media-" + Guid.NewGuid().ToString("N") + Path.GetExtension(info.StoredFileName));
        try
        {
            await using (var source = await fileStorage.OpenReadAsync(
                info.StoredFileName, MediaFileCategories.Originals, subPath, cancellationToken))
            await using (var target = File.Create(tempPath))
            {
                await source.CopyToAsync(target, cancellationToken);
            }

            if (info.Kind == MediaKind.Image)
            {
                var bytes = await File.ReadAllBytesAsync(tempPath, cancellationToken);
                progress.Frames = 1;
                await ProcessImageAsync(bytes, frameIndex: null, frameTimestampMs: null, subPath, progress, cancellationToken);
                return null;
            }

            long? durationMs = null;
            var sampling = new FrameSamplingOptions(options.SampleFps);
            await foreach (var frame in frameExtractor.ExtractAsync(tempPath, sampling, cancellationToken))
            {
                progress.Frames++;
                var timestampMs = (long)frame.Timestamp.TotalMilliseconds;
                durationMs = timestampMs;
                await ProcessImageAsync(frame.JpegBytes, frame.Index, timestampMs, subPath, progress, cancellationToken);
            }

            return durationMs;
        }
        finally
        {
            try
            {
                File.Delete(tempPath);
            }
            catch (IOException exception)
            {
                MediaIndexerLog.TempFileNotDeleted(logger, exception, info.AssetId, tempPath);
            }
            catch (UnauthorizedAccessException exception)
            {
                MediaIndexerLog.TempFileNotDeleted(logger, exception, info.AssetId, tempPath);
            }
        }
    }

    /// <summary>
    /// Один кадр: детекция → для каждого лица качество → (если пригодно) шаблон; вырезка сохраняется для
    /// всех лиц — оператор видит и отклонённые, с причиной (ТО-мат-07). Непригодное лицо пишется БЕЗ
    /// шаблона: в поиске оно не участвует, но в карточке носителя отображается.
    /// </summary>
    private async Task ProcessImageAsync(
        byte[] imageBytes, int? frameIndex, long? frameTimestampMs, string subPath, Progress progress,
        CancellationToken cancellationToken)
    {
        var faces = await detector.DetectAsync(imageBytes, cancellationToken);
        if (faces.Count == 0)
        {
            return;
        }

        var size = imageTools.ReadSize(imageBytes);
        foreach (var face in faces)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var quality = qualityAssessor.Assess(face, size.Width, size.Height);

            float[]? template = null;
            if (quality.Acceptable)
            {
                template = await embedder.EmbedAsync(imageBytes, face, cancellationToken);
            }
            else
            {
                progress.Rejected++;
            }

            var crop = imageTools.CropJpeg(imageBytes, face.Box);
            string cropName;
            using (var cropStream = new MemoryStream(crop))
            {
                cropName = await fileStorage.SaveAsync(
                    cropStream, ".jpg", MediaFileCategories.FaceCrops, subPath, cancellationToken);
            }

            progress.Faces.Add(new IndexedFace(frameIndex, frameTimestampMs, face, quality, template, cropName, TrackId: null));
        }
    }

    /// <summary>Счётчики прогона — доступны и в ветке ошибки, чтобы отчёт о сбое нёс, сколько успели обработать.</summary>
    private sealed class Progress
    {
        public int Frames { get; set; }

        public int Rejected { get; set; }

        public List<IndexedFace> Faces { get; } = [];
    }
}
