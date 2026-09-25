using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.BackgroundTasks;
using ISC.AI.Abstractions.Security;
using ISC.AI.Abstractions.Storage;
using ISC.AI.Modules.Media.Application.Features.Maintenance;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ISC.AI.Modules.Media.Application.Features.Assets.Commands.SnapshotFrame;

/// <summary>
/// Снимок кадра видео как НОВЫЙ носитель дела (ADR-0028, предлагаемый ТФ-МЕД-12): кадр в момент
/// <paramref name="TimestampMs"/> вырезается из оригинала без обработки (PNG, ТЭ-007) и принимается в то же дело,
/// что и источник, как фотография — с грифом и подразделением источника, реквизитами происхождения и фоновой
/// индексацией лиц. Право — как у загрузки (ТП-004).
/// </summary>
/// <remarks>
/// ПОРЯДОК ПРОВЕРОК — как у переиндексации: право → решётка на строке носителя (каталог, ТБ-020) → сужение по делам
/// субъекта (ТБ-071) → вид «видео» → момент в пределах ТОЧНОЙ длительности → дело источника. Недоступный носитель
/// неотличим от несуществующего (ТБ-021); тексты отказов не раскрывают реквизитов чужих носителей (ТД-007).
/// ДЛИТЕЛЬНОСТЬ У НОСИТЕЛЯ ДВУХ СОРТОВ: точная — от пробы ffprobe (тогда у носителя есть и частота кадров) и
/// приблизительная — таймкод последнего кадра выборки раскадровки у видео, проиндексированного до ADR-0028 или при
/// сбое пробы. Отсечка «момент за концом» до запуска ffmpeg делается только по точной; иначе — по запасной пробе,
/// а последнее слово за ffmpeg (кадра нет → отказ). Длительности может не быть вовсе (контейнер без заголовка).
/// ГРИФ И ПОДРАЗДЕЛЕНИЕ СНИМКА — ИСТОЧНИКА (они равны делу, ТБ-070): оператор их не выбирает. Оригинал читается
/// на месте через <see cref="ILocalFileLocator"/>; без локального пути — временная копия в управляемом каталоге
/// (<see cref="MediaTempFiles"/>), удаляемая в любом исходе (ТБ-064). Тот же кадр того же оригинала даёт те же
/// байты — хранилище вернёт существующий носитель (ТНД-002), и команда лишь привяжет его к делу.
/// </remarks>
/// <param name="AssetId">Носитель-источник (видео).</param>
/// <param name="TimestampMs">Момент записи, мс; номер кадра считается по частоте кадров носителя.</param>
public sealed record SnapshotFrameCommand(int AssetId, long TimestampMs)
    : IRequest<ResponseDto<SnapshotFrameResult>>, IAuditableRequest
{
    /// <summary>Отказ: носитель вне допуска, вне дел субъекта или не существует — единый текст (ТБ-020/021).</summary>
    public const string NotFoundMessage = "Носитель не найден или недоступен.";

    /// <summary>Отказ: носитель — не видеозапись (у фото и аудио кадров нет).</summary>
    public const string NotVideoMessage = "Снимок кадра возможен только с видеозаписи.";

    /// <summary>Отказ: момент дальше конца записи (по точной длительности пробы либо по ответу ffmpeg — кадра нет).</summary>
    public const string BeyondEndMessage = "Момент за концом записи: кадра нет.";

    /// <summary>Отказ: у носителя нет дела, доступного субъекту, — снимок сохранить некуда.</summary>
    public const string NotLinkedMessage = "Носитель не привязан к делу: снимок сохранить некуда.";

    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Ingest;

    /// <inheritdoc />
    public string? AuditSummary =>
        "media:snapshot:source=" + AssetId.ToString(CultureInfo.InvariantCulture)
        + ";t=" + TimestampMs.ToString(CultureInfo.InvariantCulture);

    /// <inheritdoc cref="SnapshotFrameCommand" />
    /// <remarks>
    /// <paramref name="fileLocator"/> необязателен: хранилище без локальных путей (сетевое, объектное) порт не
    /// регистрирует или возвращает <see langword="null"/> — тогда оригинал копируется во временный файл.
    /// </remarks>
    public sealed class Handler(
        IMediaAdministration administration,
        IAccessContextProvider accessProvider,
        IMediaCatalog catalog,
        ICaseScope caseScope,
        IMediaStore store,
        IFileStorage fileStorage,
        IFrameExtractor frameExtractor,
        IAuditWriter auditWriter,
        IBackgroundTaskQueue queue,
        MediaTempFiles tempFiles,
        ILogger<Handler> logger,
        ILocalFileLocator? fileLocator = null)
        : IRequestHandler<SnapshotFrameCommand, ResponseDto<SnapshotFrameResult>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<SnapshotFrameResult>> Handle(SnapshotFrameCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (!await administration.CanUploadAsync(cancellationToken))
            {
                return ResponseDto<SnapshotFrameResult>.BadRequest("Снимок кадра доступен ролям Следователь и Администратор.");
            }

            // Fail-closed (ТБ-020/021): носитель вне допуска неотличим от несуществующего.
            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var asset = await catalog.GetAsync(command.AssetId, access, cancellationToken);
            if (asset is null)
            {
                return ResponseDto<SnapshotFrameResult>.NotFound(NotFoundMessage);
            }

            // Сужение по делам субъекта поверх решётки (ТБ-071, ТФ-ДЕЛ-03): кадр чужого носителя по перебираемому
            // идентификатору не вырезать; отказ неотличим от «не найден».
            if (!await caseScope.IsAssetAccessibleAsync(asset.Id, access, cancellationToken))
            {
                return ResponseDto<SnapshotFrameResult>.NotFound(NotFoundMessage);
            }

            if (asset.Kind != MediaKind.Video)
            {
                return ResponseDto<SnapshotFrameResult>.BadRequest(NotVideoMessage);
            }

            // Предварительная отсечка по длительности — ТОЛЬКО когда она точная, то есть от пробы ffprobe (у носителя
            // есть частота кадров). Без пробы DurationMs — таймкод последнего кадра ВЫБОРКИ раскадровки (1 к/с,
            // округлён вниз до секунды): по нему хвост записи до ~1 с отклонялся бы как «за концом», хотя кадр на
            // экране. Тогда длительность сверяется с запасной пробой ниже, а окончательное решение — за ffmpeg.
            if (asset.FrameRate is > 0 && asset.DurationMs is > 0 && command.TimestampMs > asset.DurationMs)
            {
                return ResponseDto<SnapshotFrameResult>.BadRequest(BeyondEndMessage);
            }

            // Дело источника — то, к которому привяжется снимок (ТБ-070: гриф и подразделение носителя равны делу).
            // Среди нескольких дел носителя (дедуп) берётся доступное субъекту; чужие наружу не выходят.
            var caseId = await caseScope.GetCaseIdForAssetAsync(asset.Id, access, cancellationToken);
            if (caseId is null)
            {
                return ResponseDto<SnapshotFrameResult>.BadRequest(NotLinkedMessage);
            }

            var subPath = asset.Id.ToString(CultureInfo.InvariantCulture);
            string? tempPath = null;
            try
            {
                // Оригинал читается НА МЕСТЕ (ADR-0028 п.4): ffmpeg получает путь только на чтение, без копии до
                // 200 МБ. Хранилище без локальных путей — временная копия в управляемом каталоге: удаляется в
                // finally, а после аварийной остановки хоста — уборкой при старте (ТБ-064).
                var path = fileLocator?.TryGetLocalPath(asset.StoredFileName, MediaFileCategories.Originals, subPath);
                if (path is null)
                {
                    tempPath = tempFiles.NewPath(MediaTempFiles.SnapshotPrefix, asset.StoredFileName);
                    await CopyOriginalAsync(asset.StoredFileName, subPath, tempPath, cancellationToken);
                    path = tempPath;
                }

                // Частота кадров — свойство носителя (проба при индексации, ADR-0028 п.1); у видео, проиндексированного
                // до этого, — пробой сейчас. Нет и её (видеопотока нет) — момент берётся как запрошен, без номера кадра.
                var frameRate = asset.FrameRate;
                if (frameRate is null or <= 0)
                {
                    var probe = await frameExtractor.ProbeAsync(path, cancellationToken);
                    frameRate = probe?.FrameRate;

                    // Точная длительность запасной пробы (если контейнер её сообщает) — та же отсечка, что выше;
                    // без неё решает ffmpeg: за концом записи кадра нет (null → отказ ниже).
                    if (probe?.DurationMs is { } probedDurationMs && command.TimestampMs > probedDurationMs)
                    {
                        return ResponseDto<SnapshotFrameResult>.BadRequest(BeyondEndMessage);
                    }
                }

                // Номер кадра и момент — в одном месте (VideoProbe, ADR-0028 п.2): N = round(t·fps); запрос ffmpeg —
                // на полкадра раньше номинала (устойчиво к округлению меток контейнера); в реквизиты идёт
                // НОМИНАЛЬНОЕ время кадра N — по нему ссылка «?t=» покажет тот же кадр.
                long? frameIndex = null;
                var timestampMs = command.TimestampMs;
                var at = TimeSpan.FromMilliseconds(command.TimestampMs);
                if (frameRate is { } fps && fps > 0)
                {
                    var index = VideoProbe.FrameIndexAt(command.TimestampMs, fps);
                    frameIndex = index;
                    at = VideoProbe.SeekTimeFor(index, fps);
                    timestampMs = VideoProbe.TimestampOf(index, fps);
                }

                // ТЭ-007: PNG без потерь, исходный размер, без обработки; единственное преобразование — автоповорот
                // по метке контейнера, и оно названо в реквизитах и журнале.
                var png = await frameExtractor.ExtractFrameAsync(path, at, FrameImageFormat.Png, maxSide: null, cancellationToken);
                if (png is null)
                {
                    return ResponseDto<SnapshotFrameResult>.BadRequest(BeyondEndMessage);
                }

                // Гриф и подразделение — ИСТОЧНИКА (равны делу, ТБ-070). Происхождение — в данных (слабая ссылка
                // SourceAssetId/SourceTimestampMs: уничтожение видео снимок не трогает, ТБ-064/075) и в реквизите
                // «Источник» для человека.
                var draft = new MediaAssetDraft(
                    SnapshotFrameNames.FileName(asset.OriginalFileName, frameIndex, timestampMs),
                    SnapshotFrameNames.ContentType,
                    MediaKind.Image,
                    asset.Classification,
                    asset.DivisionId,
                    Source: SnapshotFrameNames.Source(asset.Id, frameIndex, timestampMs),
                    CapturedAt: asset.CapturedAt?.AddMilliseconds(timestampMs),
                    UploadedByUserId: access.NumericSubjectId,
                    SourceAssetId: asset.Id,
                    SourceTimestampMs: timestampMs);

                // ТНД-002: тот же кадр того же оригинала — те же байты; хранилище вернёт существующий носитель.
                MediaAssetReceipt receipt;
                using (var content = new MemoryStream(png))
                {
                    receipt = await store.ReceiveAsync(draft, content, cancellationToken);
                }

                await caseScope.LinkAssetAsync(caseId.Value, receipt.AssetId, place: null, access.NumericSubjectId, cancellationToken);

                if (!receipt.Duplicate)
                {
                    // Индексация лиц — как у загруженного фото: та же задача, тот же конвейер (закрытое дело
                    // отказывает в нём так же, ТБ-074). Захватываем только примитив: scope запроса к моменту
                    // выполнения уже закрыт.
                    var snapshotAssetId = receipt.AssetId;
                    await queue.EnqueueAsync(
                        UploadMediaCommand.IndexingTaskKind,
                        async (sp, ct) => await sp.GetRequiredService<IMediaIndexer>().IndexAsync(snapshotAssetId, ct),
                        cancellationToken);
                }

                // ТБ-030: помимо записи сквозного поведения («кто и что запросил») — запись на НОВЫЙ носитель с
                // грифом и подразделением источника: какой кадр, откуда и что с ним сделано. Пишется и для
                // дубликата — кадр вырезан и привязан к делу.
                await auditWriter.WriteAsync(
                    new AuditEntry(
                        AuditAction.Ingest,
                        asset.Classification,
                        access.NumericSubjectId,
                        ObjectRef: "media:asset:" + receipt.AssetId.ToString(CultureInfo.InvariantCulture) + ":snapshot",
                        DivisionId: asset.DivisionId,
                        PayloadSensitive: SnapshotFrameNames.AuditPayload(asset.Id, frameIndex, timestampMs)),
                    cancellationToken);

                // Имя файла в хранилище — через каталог (под той же решёткой): квитанция приёма несёт только
                // идентификатор. Снимок носит гриф и подразделение источника, который субъект только что прочитал,
                // так что виден ему же; пустое имя — только при гонке с уничтожением носителя.
                var snapshot = await catalog.GetAsync(receipt.AssetId, access, cancellationToken);
                var result = new SnapshotFrameResult(
                    receipt.AssetId, snapshot?.StoredFileName ?? string.Empty, receipt.Duplicate, frameIndex, timestampMs);
                var number = receipt.AssetId.ToString(CultureInfo.InvariantCulture);
                return ResponseDto<SnapshotFrameResult>.Ok(
                    result,
                    receipt.Duplicate
                        ? "Такой кадр уже есть в деле: носитель № " + number + "."
                        : "Снимок кадра сохранён как носитель № " + number + ".");
            }
            finally
            {
                // Временная копия оригинала — в любом исходе (успех, отказ, сбой ffmpeg, отмена).
                if (tempPath is not null)
                {
                    DeleteTempFile(asset.Id, tempPath);
                }
            }
        }

        /// <summary>Копирует оригинал носителя из хранилища во временный файл <paramref name="tempPath"/>.</summary>
        private async Task CopyOriginalAsync(string storedFileName, string subPath, string tempPath, CancellationToken cancellationToken)
        {
            await using var source = await fileStorage.OpenReadAsync(
                storedFileName, MediaFileCategories.Originals, subPath, cancellationToken);
            await using var target = File.Create(tempPath);
            await source.CopyToAsync(target, cancellationToken);
        }

        /// <summary>
        /// Удаляет временную копию без проброса: неудача пишется в журнал с путём (файл удалит уборка при следующем
        /// старте хоста, до того — вручную), а падение здесь скрыло бы исход команды.
        /// </summary>
        private void DeleteTempFile(int assetId, string tempPath)
        {
            try
            {
                File.Delete(tempPath);
            }
            catch (IOException exception)
            {
                SnapshotFrameLog.TempFileNotDeleted(logger, exception, assetId, tempPath);
            }
            catch (UnauthorizedAccessException exception)
            {
                SnapshotFrameLog.TempFileNotDeleted(logger, exception, assetId, tempPath);
            }
        }
    }
}

/// <summary>Итог снимка кадра: созданный (или найденный по хешу) носитель и его реквизиты для сообщения оператору.</summary>
/// <param name="AssetId">Носитель-снимок (фото) в схеме <c>media</c>.</param>
/// <param name="StoredFileName">Имя файла в хранилище — для ссылки «скачать» (<c>MediaFileRoutes.Build</c>).</param>
/// <param name="Duplicate"><see langword="true"/> — такой кадр уже есть (дедуп по хешу, ТНД-002): новый носитель не создан.</param>
/// <param name="FrameIndex">Номер кадра по нативной частоте (<see cref="VideoProbe.FrameIndexAt(long, double)"/>); <see langword="null"/> — частота неизвестна (результат всё равно успешный: момент взят как запрошен).</param>
/// <param name="TimestampMs">Момент записи, из которого взят кадр, мс.</param>
public sealed record SnapshotFrameResult(int AssetId, string StoredFileName, bool Duplicate, long? FrameIndex, long TimestampMs);
