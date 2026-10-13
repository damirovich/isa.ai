using System.Globalization;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.BackgroundTasks;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Application.Features.Assets;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;

namespace ISC.AI.Modules.Media.Application.Features.Import;

/// <summary>Задание в работе и допуск оператора, прочитанный перед этим шагом.</summary>
/// <param name="Job">Задание.</param>
/// <param name="Access">Действующий допуск оператора (свежее чтение, ТБ-016).</param>
public sealed record MediaImportContext(MediaImportJobRun Job, AccessContext Access);

/// <summary>Итог шага исполнителя.</summary>
public enum MediaImportStep
{
    /// <summary>Строка разобрана (принята, дубликат или отказ).</summary>
    Processed = 1,

    /// <summary>Строк не осталось — задание завершено.</summary>
    JobCompleted = 2,

    /// <summary>Приём прерван остановкой задания или сервера; строка возвращена в очередь.</summary>
    Interrupted = 3,
}

/// <summary>
/// Сбой при приёме строки, не связанный с самим файлом (БД, журнал): строка возвращена в «ждёт приёма», исполнитель
/// повторит её позже, а после нескольких сбоев подряд — отклонит с причиной (<see cref="MediaImportRunner.RejectAsync"/>).
/// </summary>
public sealed class MediaImportItemException : Exception
{
    /// <summary>Создаёт исключение для строки.</summary>
    /// <param name="work">Строка, которую принимали.</param>
    /// <param name="innerException">Причина.</param>
    public MediaImportItemException(MediaImportWorkItem work, Exception innerException)
        : base("Сбой при приёме строки импорта: " + innerException?.Message, innerException)
    {
        Work = work;
    }

    /// <summary>Создаёт исключение без строки (для анализаторов).</summary>
    public MediaImportItemException()
    {
    }

    /// <summary>Создаёт исключение с сообщением.</summary>
    /// <param name="message">Сообщение.</param>
    public MediaImportItemException(string message)
        : base(message)
    {
    }

    /// <summary>Создаёт исключение с сообщением и причиной.</summary>
    /// <param name="message">Сообщение.</param>
    /// <param name="innerException">Причина.</param>
    public MediaImportItemException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Строка, которую принимали.</summary>
    public MediaImportWorkItem? Work { get; }
}

/// <summary>
/// Исполнитель задания импорта архива (ADR-0041 п. 5–6, 8): берёт следующую строку и принимает файл ОТ ИМЕНИ ОПЕРАТОРА
/// тем же путём, что и загрузка (<see cref="MediaIntake"/>), с проверками на каждом файле.
/// </summary>
/// <remarks>
/// <para>
/// ИНВАРИАНТЫ БЕЗОПАСНОСТИ. (1) Перед каждым файлом заново читаются учётная запись и допуск оператора
/// (<see cref="IUserAccessReader"/>, ТБ-016) и его право «Импорт архива»; утрата любого — остановка задания с причиной,
/// а не отказ по каждой строке. (2) Дело должно быть доступно оператору (решётка и роль, ТБ-020/071); недоступное
/// неотличимо от несуществующего. (3) Гриф и подразделение — дела (ТБ-070), и только те, что оператор подтвердил
/// (ТБ-024): изменились — строка не принимается. (4) Файл ищется под папкой задания, а папка — под каталогом из
/// настройки (<see cref="MediaImportPaths"/>); каталог исключили из настройки — задание останавливается. (5) Каждый
/// файл — запись неизменяемого журнала (ТБ-030, цепочка хранения ТБ-077): кто, задание, строка, дело, носитель, хеш
/// до и после переноса. Запись журнала — ДО записи итога строки: строка без записи журнала не завершается и будет
/// принята заново.
/// </para>
/// </remarks>
public sealed class MediaImportRunner(
    IMediaImportStore imports,
    IMediaAdministration administration,
    IUserAccessReader accessReader,
    ICaseScope caseScope,
    IMediaStore store,
    IAuditWriter audit,
    MediaImportOptions importOptions,
    MediaUploadOptions uploadOptions,
    TimeProvider time)
{
    /// <summary>Причина остановки: каталог исключён из настройки.</summary>
    public const string FolderNotAllowedReason =
        "Каталог архива больше не входит в список разрешённых для импорта (" + MediaImportOptions.RootsKey + ").";

    /// <summary>Причина остановки: нет допуска или учётная запись отключена.</summary>
    public const string NoClearanceReason =
        "У оператора, запустившего импорт, нет действующего допуска или его учётная запись отключена.";

    /// <summary>Причина остановки: нет права.</summary>
    public const string NoPermissionReason = "У оператора, запустившего импорт, больше нет права «Импорт архива материалов».";

    /// <summary>Отказ строки: дело недоступно.</summary>
    public const string CaseUnavailableReason = "дело недоступно оператору или удалено";

    /// <summary>Отказ строки: гриф или подразделение дела изменились после подтверждения.</summary>
    public const string CaseChangedReason =
        "гриф или подразделение дела изменились после подтверждения — проверьте дело и импортируйте строку заново";

    /// <summary>
    /// Берёт задание в работу и проверяет то, без чего не принимается ни один файл: папка под разрешённым каталогом,
    /// допуск и право оператора. Не прошло — задание останавливается с причиной.
    /// </summary>
    /// <returns>Задание с допуском оператора или <see langword="null"/>, если работы нет или задание остановлено.</returns>
    public async Task<MediaImportContext?> PrepareAsync(CancellationToken cancellationToken = default)
    {
        var job = await imports.StartNextJobAsync(cancellationToken);
        if (job is null)
        {
            return null;
        }

        if (!importOptions.UsableRoots.Any(root => MediaImportPaths.IsUnder(job.FolderPath, root)))
        {
            await StopBySystemAsync(job, FolderNotAllowedReason, cancellationToken);
            return null;
        }

        // ТБ-016: допуск и право — свежим чтением перед каждым файлом, а не снимком на момент запуска.
        var access = await accessReader.ReadAsync(job.OperatorUserId, cancellationToken);
        if (access is null)
        {
            await StopBySystemAsync(job, NoClearanceReason, cancellationToken);
            return null;
        }

        if (!await administration.UserCanImportArchiveAsync(job.OperatorUserId, cancellationToken))
        {
            await StopBySystemAsync(job, NoPermissionReason, cancellationToken);
            return null;
        }

        return new MediaImportContext(job, access);
    }

    /// <summary>Принимает следующую строку задания.</summary>
    /// <param name="context">Задание и допуск оператора (<see cref="PrepareAsync"/>).</param>
    /// <param name="cancellationToken">
    /// Отмена — остановка задания или сервера: строка возвращается в очередь, временный файл удаляется хранилищем.
    /// </param>
    public async Task<MediaImportStep> RunItemAsync(MediaImportContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var work = await imports.TakeNextItemAsync(context.Job.JobId, cancellationToken);
        if (work is null)
        {
            if (await imports.TryCompleteJobAsync(context.Job.JobId, cancellationToken))
            {
                await WriteJobAuditAsync(context.Job, "завершено: все строки разобраны", context.Access.MaxClassification, cancellationToken);
            }

            return MediaImportStep.JobCompleted;
        }

        try
        {
            var outcome = await AcceptAsync(context, work, cancellationToken);

            // Журнал — до итога строки: не записался журнал — строка вернётся в очередь и будет принята заново.
            await WriteItemAuditAsync(context, work, outcome, cancellationToken);
            await imports.CompleteItemAsync(work.ItemId, outcome, cancellationToken);
            return MediaImportStep.Processed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await imports.ReturnItemAsync(work.ItemId, CancellationToken.None);
            return MediaImportStep.Interrupted;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await imports.ReturnItemAsync(work.ItemId, CancellationToken.None);
            throw new MediaImportItemException(work, exception);
        }
    }

    /// <summary>
    /// Отклоняет строку, на которой приём раз за разом срывался не из-за файла (<see cref="MediaImportItemException"/>):
    /// иначе одна строка держала бы задание бесконечно. Строка и причина — в журнале.
    /// </summary>
    public async Task RejectAsync(
        MediaImportContext context, MediaImportWorkItem work, string reason, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(work);

        var outcome = MediaImportItemOutcome.Reject(reason);
        await WriteItemAuditAsync(context, work, outcome, cancellationToken);
        await imports.CompleteItemAsync(work.ItemId, outcome, cancellationToken);
    }

    private async Task<MediaImportItemOutcome> AcceptAsync(
        MediaImportContext context, MediaImportWorkItem work, CancellationToken cancellationToken)
    {
        var draft = work.Draft;

        // ТБ-020/071: дело — только доступное оператору сейчас.
        var caseItem = await caseScope.GetCaseAsync(draft.CaseId, context.Access, cancellationToken);
        if (caseItem is null)
        {
            return MediaImportItemOutcome.Reject(CaseUnavailableReason);
        }

        // ТБ-024: подтверждались эти гриф и подразделение — с другими файл не принимается.
        if (caseItem.Classification != draft.Classification || caseItem.DivisionId != draft.DivisionId)
        {
            return MediaImportItemOutcome.Reject(CaseChangedReason);
        }

        var file = new MediaImportPaths().ResolveFile(context.Job.FolderPath, draft.RelativePath, out var fileProblem);
        if (file is null)
        {
            return MediaImportItemOutcome.Reject(fileProblem ?? MediaImportPaths.NotFoundMessage);
        }

        FileStream content;
        try
        {
            content = new FileStream(
                file.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return MediaImportItemOutcome.Reject("файл не открыт: " + exception.Message);
        }

        MediaIntakeResult result;
        try
        {
            var intake = new MediaIntake(store, caseScope, uploadOptions);
            result = await intake.ReceiveAsync(
                new MediaIntakeRequest(
                    caseItem,
                    Path.GetFileName(file.RelativePath),
                    draft.ContentType,
                    file.Length,
                    draft.Source,
                    draft.CapturedAt,
                    draft.Place,
                    context.Job.OperatorUserId,
                    draft.ExpectedSha256,
                    work.ItemId,
                    CaptureTimeZone: time.LocalTimeZone),
                content,
                cancellationToken);
        }
        catch (IOException exception)
        {
            // Повреждённый сектор, отключённый диск: строка — отказ с причиной, задание идёт дальше.
            return MediaImportItemOutcome.Reject("ошибка чтения файла: " + exception.Message);
        }

        if (result.Receipt is not { } receipt)
        {
            return MediaImportItemOutcome.Reject(result.Message ?? MediaIntake.UnsupportedFormatMessage);
        }

        if (!receipt.Duplicate)
        {
            return new MediaImportItemOutcome(
                MediaImportItemStatus.Accepted, null, receipt.AssetId, receipt.ContentHash, MediaImportProcessingState.Waiting);
        }

        // Приём этой строки уже был и записал носитель, но итог строки не успел (авария): носитель свой, его
        // обработку подаёт задание. Иначе — настоящий дубликат: носитель уже обработан или обрабатывается своим путём.
        var own = await imports.IsAssetFromItemAsync(receipt.AssetId, work.ItemId, cancellationToken);
        return own
            ? new MediaImportItemOutcome(MediaImportItemStatus.Accepted, null, receipt.AssetId, receipt.ContentHash, MediaImportProcessingState.Waiting)
            : new MediaImportItemOutcome(MediaImportItemStatus.Duplicate, null, receipt.AssetId, receipt.ContentHash, MediaImportProcessingState.None);
    }

    private async Task StopBySystemAsync(MediaImportJobRun job, string reason, CancellationToken cancellationToken)
    {
        if (await imports.StopAsync(job.JobId, reason, cancellationToken))
        {
            await WriteJobAuditAsync(job, "остановлено системой: " + reason, ClassificationLevels.Max, cancellationToken);
        }
    }

    private Task WriteJobAuditAsync(MediaImportJobRun job, string what, short classification, CancellationToken cancellationToken) =>
        audit.WriteAsync(
            new AuditEntry(
                AuditAction.Ingest,
                classification,
                SubjectId: null,
                ObjectRef: "media:import:job=" + job.JobId.ToString(CultureInfo.InvariantCulture),
                PayloadSensitive: $"импорт архива, задание {job.JobId.ToString(CultureInfo.InvariantCulture)} "
                    + $"(запустил пользователь {job.OperatorUserId.ToString(CultureInfo.InvariantCulture)}): {what}"),
            cancellationToken);

    private Task WriteItemAuditAsync(
        MediaImportContext context, MediaImportWorkItem work, MediaImportItemOutcome outcome, CancellationToken cancellationToken)
    {
        var draft = work.Draft;
        var result = outcome.Status switch
        {
            MediaImportItemStatus.Accepted => "принят",
            MediaImportItemStatus.Duplicate => "дубликат",
            _ => "отказ",
        };
        var objectRef = string.Create(
            CultureInfo.InvariantCulture,
            $"media:import:job={work.JobId};row={draft.RowNumber};case={draft.CaseId};asset={outcome.AssetId?.ToString(CultureInfo.InvariantCulture) ?? "-"};result={result}");

        // Гриф записи — не ниже грифа дела и допуска субъекта (как у сквозного аудита): путь файла сам может быть
        // сведением ограниченного доступа.
        return audit.WriteAsync(
            new AuditEntry(
                AuditAction.Ingest,
                Math.Max(context.Access.MaxClassification, draft.Classification),
                context.Job.OperatorUserId,
                objectRef,
                draft.DivisionId,
                PayloadSensitive: $"импорт архива: {result}; файл «{draft.RelativePath}»; дело № {draft.CaseNumber}; "
                    + $"sha256 до переноса={draft.ExpectedSha256 ?? "—"}; после={outcome.ContentSha256 ?? "—"}"
                    + (outcome.Reason is null ? string.Empty : "; причина: " + outcome.Reason)),
            cancellationToken);
    }
}

/// <summary>
/// Подача носителей архива в общую очередь обработки малыми порциями (ADR-0041 п. 7): в очереди не больше
/// <see cref="MediaImportOptions.ProcessingSlots"/> носителей архива, следующий — когда предыдущий обработан. Новые
/// материалы следователей ставятся в ту же очередь сразу и ждут не дольше обработки одного носителя архива.
/// </summary>
public sealed class MediaImportFeeder(
    IMediaImportStore imports,
    IMediaStore store,
    IBackgroundTaskQueue queue,
    IBackgroundTaskStore tasks,
    MediaImportOptions options)
{
    /// <summary>Отмечает завершённую обработку и подаёт следующие носители на освободившиеся места.</summary>
    /// <returns>Сколько носителей подано.</returns>
    public async Task<int> FeedAsync(CancellationToken cancellationToken = default)
    {
        var queued = await imports.ListQueuedProcessingAsync(cancellationToken);
        var running = 0;
        foreach (var item in queued)
        {
            if (await IsFinishedAsync(item.IndexTaskId, cancellationToken) && await IsFinishedAsync(item.TranscriptTaskId, cancellationToken))
            {
                await imports.MarkProcessingDoneAsync(item.ItemId, cancellationToken);
            }
            else
            {
                running++;
            }
        }

        var free = options.ProcessingSlots - running;
        if (free <= 0)
        {
            return 0;
        }

        var next = await imports.ListWaitingProcessingAsync(free, cancellationToken);
        foreach (var item in next)
        {
            // Расшифровка носителя архива стоит «в очереди» с приёма либо «ошибка, прервано перезапуском» — после
            // аварии посреди обработки. Условный перевод вернёт её в «в очереди»; уже «в очереди» — оставит как есть.
            if (item.Kind is MediaKind.Audio or MediaKind.Video)
            {
                await store.TryMarkTranscriptionPendingAsync(item.AssetId, cancellationToken);
            }

            var started = await MediaProcessing.EnqueueAsync(queue, item.AssetId, item.Kind, cancellationToken);
            await imports.MarkProcessingQueuedAsync(item.ItemId, started.IndexTaskId, started.TranscriptTaskId, cancellationToken);
        }

        return next.Count;
    }

    /// <summary>Задача завершилась (успешно или ошибкой) или её нет.</summary>
    public async Task<bool> IsFinishedAsync(Guid? taskId, CancellationToken cancellationToken = default)
    {
        if (taskId is not { } id)
        {
            return true;
        }

        var info = await tasks.GetAsync(id, cancellationToken);
        return info is null
            || info.Status is BackgroundTaskStatus.Completed or BackgroundTaskStatus.Failed;
    }
}
