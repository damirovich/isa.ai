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

namespace ISC.AI.Modules.Media.Application.Features.Indexing;

/// <summary>
/// Конвейер индексации носителя (ТП-005, ТО-мат-05): исходник из хранилища → (для видео) проба потоков — без
/// видеопотока носитель переводится в аудиозаписи (ADR-0026), иначе носитель получает нативную частоту кадров, точную
/// длительность и размер кадра (ADR-0028) — и раскадровка (ТО-мат-06) → детекция лиц → оценка качества (ТО-мат-07) →
/// векторизация пригодных → вырезки → атомарная запись шаблонов с грифом носителя (ТБ-070). Исполняется фоновой очередью ядра из
/// per-operation scope; повторный запуск идемпотентен — шаблоны перезаписываются хранилищем одной
/// транзакцией, прежние вырезки снимаются ПОСЛЕ её фиксации (половинчатого состояния нет, ТП-005).
/// </summary>
/// <remarks>
/// Ошибка любого шага НЕ пробрасывается: фиксируется в статусе носителя (<see cref="IMediaStore.FailIndexingAsync"/>)
/// и возвращается в <see cref="MediaIndexResult"/> — фоновой задаче незачем падать, оператор видит причину в
/// карточке (ТФ-МЕД-02); вырезки, сохранённые сорвавшимся прогоном, снимаются. Отмена — фиксируется как
/// «индексация отменена» и пробрасывается (воркер останавливается штатно). Результат индексации —
/// аудируемое событие (ТО-инф-11, ТБ-030): запись <see cref="AuditAction.Ingest"/> с грифом носителя, без
/// субъекта (конвейер работает от имени системы). После успешной индексации носителя с лицами — предложения
/// связей с фигурантами его дела (<see cref="IPersonSuggester"/>, ТФ-ПЕР-09); их сбой индексацию не отменяет.
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
    ICaseScope caseScope,
    MediaSearchOptions options,
    MediaTempFiles tempFiles,
    IPersonSuggester suggester,
    ILogger<MediaIndexer> logger) : IMediaIndexer
{
    /// <summary>Причина отказа индексации аудиозаписи (результат задачи; статус носителя не меняется).</summary>
    public const string NotApplicableToAudioError = "Поиск по лицу к аудиозаписи неприменим: лиц в ней нет.";

    /// <summary>Причина отказа индексации «видео» без видеопотока (результат задачи; носитель переведён в аудио).</summary>
    public const string NoVideoStreamError =
        "Видеопотока в файле нет (только звук): носитель переведён в аудиозаписи, поиск по лицу неприменим.";

    /// <inheritdoc />
    public async Task<MediaIndexResult> IndexAsync(int assetId, CancellationToken cancellationToken = default)
    {
        var info = await store.GetForIndexingAsync(assetId, cancellationToken);
        if (info is null)
        {
            MediaIndexerLog.AssetNotFound(logger, assetId);
            return new MediaIndexResult(false, 0, 0, 0, "Носитель не найден.");
        }

        // ADR-0026: в аудиозаписи лиц нет — конвейер биометрии к ней НЕПРИМЕНИМ. Выход без каких-либо
        // изменений: ни статуса «в обработке», ни «проиндексировано с нулём лиц», ни записи в журнал —
        // иначе журнал утверждал бы, что биометрия обрабатывалась. Статус носителя уже «неприменимо»
        // (ставится при приёме). Проверка здесь, а не только в сценариях: задача может прийти любым путём.
        if (info.Kind == MediaKind.Audio)
        {
            MediaIndexerLog.NotApplicableToAudio(logger, assetId);
            return new MediaIndexResult(false, 0, 0, 0, NotApplicableToAudioError);
        }

        // ПОСЛЕДНИЙ РУБЕЖ регламента ТБ-074 (ADR-0024): закрытое дело лишилось шаблонов, и строить их
        // заново нельзя — иначе биометрия возвращается в поиск без основания. Проверка стоит ЗДЕСЬ, а не
        // только в сценариях: в конвейер задача попадает и из загрузки, и из переиндексации, и из
        // восстановления осиротевших задач после рестарта, а обойти регламент не должен ни один путь.
        if (!await caseScope.IsBiometricIndexingAllowedAsync(assetId, cancellationToken))
        {
            MediaIndexerLog.IndexingForbiddenByClosedCase(logger, assetId);
            return new MediaIndexResult(
                false, 0, 0, 0,
                "Дело носителя закрыто: шаблоны удалены регламентом (ТБ-074) и заново не строятся.");
        }

        var progress = new Progress();
        var subPath = assetId.ToString(CultureInfo.InvariantCulture);
        string? tempPath = null;
        try
        {
            // Исходник → временная копия (раскадровщик и проба потоков — внешние процессы, работают с путём).
            // Копия — в управляемом каталоге (ТБ-064): переживи она аварийную остановку, её удалит уборка при
            // старте хоста; расширение — через фильтр (путь уходит ffmpeg текстом командной строки).
            tempPath = tempFiles.NewPath(MediaTempFiles.FramesPrefix, info.StoredFileName);
            await CopySourceAsync(info, subPath, tempPath, cancellationToken);

            // ADR-0026 п.6: «видео» без видеопотока — голосовое .3gp (браузер объявляет его video/3gpp), звук в
            // mp4/webm. Лиц в нём нет и быть не может: носитель переводится в аудиозаписи с «поиск по лицу
            // неприменим» ДО статуса «в обработке» — ни «проиндексировано с нулём лиц», ни записи в журнал (журнал
            // не должен утверждать, что биометрия обрабатывалась), ни «ошибки» по сбою ffmpeg на входе без картинки.
            // Расшифровка звука (поставлена при загрузке отдельной задачей) идёт своим ходом.
            if (info.Kind == MediaKind.Video && !await frameExtractor.HasVideoStreamAsync(tempPath, cancellationToken))
            {
                await store.ReclassifyAsAudioAsync(assetId, cancellationToken);
                MediaIndexerLog.NoVideoStream(logger, assetId);
                return new MediaIndexResult(false, 0, 0, 0, NoVideoStreamError);
            }

            await store.MarkProcessingAsync(assetId, cancellationToken);

            // ADR-0028 п.1: проба видеопотока — нативная частота кадров (шаг «на кадр», номер кадра), точная
            // длительность и размер кадра после автоповорота — для покадрового просмотра и снимка кадра. Сбой пробы
            // индексацию НЕ валит: поля носителя остаются прежними, длительность возьмётся из раскадровки.
            var probe = info.Kind == MediaKind.Video ? await TryProbeAsync(assetId, tempPath, cancellationToken) : null;

            long? durationMs = await ProcessSourceAsync(info, tempPath, subPath, progress, cancellationToken);

            // Строки лиц/шаблонов заменяются одной транзакцией; ТОЛЬКО после её фиксации снимаем вырезки
            // прежнего прогона (ТП-005: файл без строки безвреден, строка без файла — нет; при сбое до этой
            // точки старые лица остаются с файлами, а новые вырезки — сироты — удаляются в catch).
            // Длительность по раскадровке — запасной источник (пишется, только если у носителя её ещё нет);
            // проба — поверх прежних значений (переиндексация обновляет их).
            await store.CompleteIndexingAsync(
                assetId, progress.Faces, detector.ModelVersion, embedder.ModelVersion, durationMs, probe, cancellationToken);
            await DeleteCropsAsync(assetId, subPath, info.ExistingCropFileNames);

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

            // ТФ-ПЕР-09: лица нового носителя — с эталонами фигурантов его дела; совпадения — в очередь верификации.
            // Шаблоны уже записаны и в журнале: сбой предложений индексацию НЕ отменяет (их можно получить повтором).
            if (progress.Faces.Count > 0)
            {
                await SuggestPersonsAsync(assetId, cancellationToken);
            }

            return new MediaIndexResult(true, progress.Frames, progress.Faces.Count, progress.Rejected);
        }
        catch (OperationCanceledException)
        {
            // Отмена: носитель не должен зависнуть в «в обработке» — фиксируем «отменено» (повтор — вручную,
            // ТФ-МЕД-02), снимаем новые вырезки-сироты и пробрасываем, чтобы воркер остановился штатно.
            MediaIndexerLog.Cancelled(logger, assetId, progress.Frames);
            await DeleteCropsAsync(assetId, subPath, NewCropNames(progress));
            await store.FailIndexingAsync(assetId, "индексация отменена", CancellationToken.None);
            throw;
        }
        catch (Exception exception)
        {
            MediaIndexerLog.Failed(logger, exception, assetId, progress.Frames);
            await DeleteCropsAsync(assetId, subPath, NewCropNames(progress));
            await store.FailIndexingAsync(assetId, exception.Message, cancellationToken);
            return new MediaIndexResult(false, progress.Frames, progress.Faces.Count, progress.Rejected, exception.Message);
        }
        finally
        {
            // Временная копия исходника — в любом исходе (успех, сбой, отмена, «нет видеопотока»).
            if (tempPath is not null)
            {
                DeleteTempFile(assetId, tempPath);
            }
        }
    }

    /// <summary>
    /// Предложения связей с фигурантами (ТФ-ПЕР-09) после успешной индексации. Любой сбой, кроме отмены, только
    /// журналируется: индексация уже зафиксирована, и откатывать её из-за подсказки нельзя.
    /// </summary>
    private async Task SuggestPersonsAsync(int assetId, CancellationToken cancellationToken)
    {
        try
        {
            await suggester.SuggestAsync(assetId, SuggestionTrigger.Indexing, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            MediaIndexerLog.SuggestionsFailed(logger, exception, assetId);
        }
    }

    /// <summary>Имена вырезок, сохранённых ТЕКУЩИМ прогоном (для компенсации при сбое до фиксации строк).</summary>
    private static List<string> NewCropNames(Progress progress)
    {
        var names = new List<string>(progress.Faces.Count);
        foreach (var face in progress.Faces)
        {
            if (face.CropStoredFileName is { } name)
            {
                names.Add(name);
            }
        }

        return names;
    }

    /// <summary>
    /// Снимает вырезки с диска без отмены (компенсация должна дойти до конца) и без проброса: файл-сирота
    /// хуже не делает, а падение здесь скрыло бы исходную ошибку или уже зафиксированный успех.
    /// </summary>
    private async Task DeleteCropsAsync(int assetId, string subPath, IReadOnlyList<string> cropFileNames)
    {
        foreach (var crop in cropFileNames)
        {
            try
            {
                await fileStorage.DeleteAsync(crop, MediaFileCategories.FaceCrops, subPath, CancellationToken.None);
            }
            catch (IOException exception)
            {
                MediaIndexerLog.CropNotDeleted(logger, exception, assetId, crop);
            }
            catch (UnauthorizedAccessException exception)
            {
                MediaIndexerLog.CropNotDeleted(logger, exception, assetId, crop);
            }
        }
    }

    /// <summary>
    /// Проба видеопотока (ADR-0028) без проброса: любой сбой ffprobe (инструмент не найден, файл не разобран)
    /// пишется в журнал и даёт <see langword="null"/> — раскадровка и лица важнее полей покадрового просмотра, и
    /// носитель не должен уйти в «ошибка» из-за них. Отмена пробрасывается: воркер останавливается штатно.
    /// </summary>
    private async Task<VideoProbe?> TryProbeAsync(int assetId, string path, CancellationToken cancellationToken)
    {
        try
        {
            return await frameExtractor.ProbeAsync(path, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            MediaIndexerLog.ProbeFailed(logger, exception, assetId);
            return null;
        }
    }

    /// <summary>Копирует исходник носителя из хранилища во временный файл <paramref name="tempPath"/>.</summary>
    private async Task CopySourceAsync(
        MediaAssetIndexingInfo info, string subPath, string tempPath, CancellationToken cancellationToken)
    {
        await using var source = await fileStorage.OpenReadAsync(
            info.StoredFileName, MediaFileCategories.Originals, subPath, cancellationToken);
        await using var target = File.Create(tempPath);
        await source.CopyToAsync(target, cancellationToken);
    }

    /// <summary>
    /// Прогоняет временную копию исходника через детектор/векторизатор: изображение — одним кадром, видео —
    /// раскадровкой. Возвращает длительность видео (последний таймкод выборки — запасной источник, если проба
    /// ADR-0028 не удалась) либо <see langword="null"/> для изображения.
    /// </summary>
    private async Task<long?> ProcessSourceAsync(
        MediaAssetIndexingInfo info, string tempPath, string subPath, Progress progress, CancellationToken cancellationToken)
    {
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

        // ТФ-ПЕР-02, ADR-0037: одно лицо на соседних кадрах — один трек; по нему появление показывается отрезком.
        var tracked = FaceTracker.Assign(progress.Faces, options.TrackMinSimilarity, options.TrackMaxGapMs);
        progress.Faces.Clear();
        progress.Faces.AddRange(tracked);

        return durationMs;
    }

    /// <summary>
    /// Удаляет временную копию без проброса: неудача пишется в журнал с путём (файл удалит уборка при следующем
    /// старте хоста, до того — вручную), а падение здесь скрыло бы исход индексации.
    /// </summary>
    private void DeleteTempFile(int assetId, string tempPath)
    {
        try
        {
            File.Delete(tempPath);
        }
        catch (IOException exception)
        {
            MediaIndexerLog.TempFileNotDeleted(logger, exception, assetId, tempPath);
        }
        catch (UnauthorizedAccessException exception)
        {
            MediaIndexerLog.TempFileNotDeleted(logger, exception, assetId, tempPath);
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
