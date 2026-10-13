using System.Globalization;
using System.Runtime.CompilerServices;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Application.Features.Assets;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.Media.Application.Features.Import;

/// <summary>Что показывает экран импорта до начала работы.</summary>
/// <param name="Roots">Каталоги из настройки — используемые и исключённые с причиной.</param>
/// <param name="ProcessingSlots">Сколько носителей архива одновременно в общей очереди обработки.</param>
/// <param name="MaxImageBytes">Предел фото, байт.</param>
/// <param name="MaxStreamedBytes">Предел видео и аудио, байт.</param>
/// <param name="DocumentTypes">Типы для документов архива (ADR-0042); пусто — документы этот экземпляр не принимает.</param>
public sealed record MediaImportSetup(
    IReadOnlyList<MediaImportRoot> Roots, int ProcessingSlots, long MaxImageBytes, long MaxStreamedBytes,
    IReadOnlyList<ArchiveDocumentType>? DocumentTypes = null);

/// <summary>Вложенные папки выбранной папки архива (для выбора на экране).</summary>
/// <param name="Folder">Полный путь выбранной папки.</param>
/// <param name="Subfolders">Имена вложенных папок (без скрытых, системных и ссылок), не больше <see cref="MediaImportFolders.MaxSubfolders"/>.</param>
/// <param name="Truncated">Папок больше предела — показаны первые.</param>
public sealed record MediaImportFolderListing(string Folder, IReadOnlyList<string> Subfolders, bool Truncated);

/// <summary>Файл для скачивания (черновик описи, отчёт).</summary>
/// <param name="Content">Содержимое.</param>
/// <param name="FileName">Имя файла.</param>
/// <param name="RowCount">Строк в файле.</param>
/// <param name="Truncated">Строк больше предела — записаны первые.</param>
public sealed record MediaImportFile(byte[] Content, string FileName, int RowCount, bool Truncated = false);

/// <summary>Итог проверки описи для экрана.</summary>
/// <param name="Manifest">Как прочитана опись (кодировка, лишние столбцы, ошибка чтения).</param>
/// <param name="Plan">Итог проверки; <see langword="null"/> — опись не прочитана (<see cref="ManifestReadResult.Error"/>).</param>
public sealed record MediaImportCheck(ManifestReadResult Manifest, MediaImportPlan? Plan);

/// <summary>Задание со страницей строк.</summary>
/// <param name="Job">Сводка.</param>
/// <param name="Items">Строки под решёткой допуска смотрящего.</param>
public sealed record MediaImportJobDetails(MediaImportJobSummary Job, MediaImportItemPage Items);

/// <summary>Общие правила сценариев импорта: право, папка, тексты отказов.</summary>
public static class MediaImportRules
{
    /// <summary>Отказ: нет права.</summary>
    public const string NoPermissionMessage = "Импорт архива доступен по праву «Импорт архива материалов» (по умолчанию — Администратор).";

    /// <summary>Отказ: субъект не из реестра пользователей.</summary>
    public const string NoUserMessage = "Импорт запускается только учётной записью реестра пользователей.";

    /// <summary>Отказ: нет подтверждения грифа и подразделения.</summary>
    public const string NotConfirmedMessage = "Подтвердите, что гриф и подразделение каждого дела проверены (ТБ-024).";

    /// <summary>Отказ: итог проверки изменился.</summary>
    public const string PlanChangedMessage =
        "С момента проверки изменились дела, их гриф или подразделение либо файлы архива — проверьте опись заново.";

    /// <summary>Отказ: нечего импортировать.</summary>
    public const string NothingToImportMessage = "Нечего импортировать: в описи нет строк без ошибок.";

    /// <summary>Отказ: опись пуста или не передана.</summary>
    public const string NoManifestMessage = "Загрузите опись — файл CSV.";
}

/// <summary>Каталоги архива из настройки и пределы приёма (ADR-0041).</summary>
public sealed record GetMediaImportSetupQuery : IRequest<ResponseDto<MediaImportSetup>>
{
    /// <inheritdoc cref="GetMediaImportSetupQuery" />
    public sealed class Handler(
        IMediaAdministration administration, MediaImportOptions options, MediaUploadOptions uploadOptions,
        IArchiveDocumentSink documents)
        : IRequestHandler<GetMediaImportSetupQuery, ResponseDto<MediaImportSetup>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<MediaImportSetup>> Handle(GetMediaImportSetupQuery query, CancellationToken cancellationToken)
        {
            if (!await administration.CanImportArchiveAsync(cancellationToken))
            {
                return ResponseDto<MediaImportSetup>.BadRequest(MediaImportRules.NoPermissionMessage);
            }

            // null — документы этот экземпляр не принимает; пустой список — принимает, но типов «Хранение» не заведено.
            var types = documents.IsSupported ? await documents.ListTypesAsync(cancellationToken) : null;
            return ResponseDto<MediaImportSetup>.Ok(new MediaImportSetup(
                options.Roots, options.ProcessingSlots, MediaFileRules.MaxImageBytes, uploadOptions.MaxStreamedFileBytes, types));
        }
    }
}

/// <summary>Папки внутри каталога архива — для выбора на экране.</summary>
/// <param name="Root">Каталог из настройки.</param>
/// <param name="Folder">Вложенная папка относительно каталога; пусто — сам каталог.</param>
public sealed record ListMediaImportFoldersQuery(string Root, string? Folder) : IRequest<ResponseDto<MediaImportFolderListing>>
{
    /// <inheritdoc cref="ListMediaImportFoldersQuery" />
    public sealed class Handler(IMediaAdministration administration, MediaImportOptions options)
        : IRequestHandler<ListMediaImportFoldersQuery, ResponseDto<MediaImportFolderListing>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<MediaImportFolderListing>> Handle(ListMediaImportFoldersQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);
            if (!await administration.CanImportArchiveAsync(cancellationToken))
            {
                return ResponseDto<MediaImportFolderListing>.BadRequest(MediaImportRules.NoPermissionMessage);
            }

            var folder = new MediaImportPaths().ResolveFolder(options.UsableRoots, query.Root, query.Folder, out var problem);
            if (folder is null)
            {
                return ResponseDto<MediaImportFolderListing>.BadRequest(problem ?? "Папка недоступна.");
            }

            var (names, truncated) = MediaImportFolders.ListSubfolders(folder);
            return ResponseDto<MediaImportFolderListing>.Ok(new MediaImportFolderListing(folder, names, truncated));
        }
    }
}

/// <summary>
/// Черновик описи по папке архива (ADR-0041 п. 2): все файлы допустимых форматов, реквизиты пустые. Оператор
/// дополняет его в Excel и загружает обратно. Перечень файлов архива — сведения о материалах, поэтому в журнал.
/// </summary>
/// <param name="Root">Каталог из настройки.</param>
/// <param name="Folder">Вложенная папка.</param>
/// <param name="CaseFromFirstFolder">Заполнить «дело» именем папки первого уровня (архив разложен по папкам дел).</param>
public sealed record BuildMediaImportDraftQuery(string Root, string? Folder, bool CaseFromFirstFolder)
    : IRequest<ResponseDto<MediaImportFile>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.View;

    /// <inheritdoc />
    public string? AuditSummary => $"media:import:draft;root={Root};folder={Folder}";

    /// <inheritdoc cref="BuildMediaImportDraftQuery" />
    public sealed class Handler(IMediaAdministration administration, MediaImportOptions options)
        : IRequestHandler<BuildMediaImportDraftQuery, ResponseDto<MediaImportFile>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<MediaImportFile>> Handle(BuildMediaImportDraftQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);
            if (!await administration.CanImportArchiveAsync(cancellationToken))
            {
                return ResponseDto<MediaImportFile>.BadRequest(MediaImportRules.NoPermissionMessage);
            }

            var folder = new MediaImportPaths().ResolveFolder(options.UsableRoots, query.Root, query.Folder, out var problem);
            if (folder is null)
            {
                return ResponseDto<MediaImportFile>.BadRequest(problem ?? "Папка недоступна.");
            }

            var (rows, truncated) = MediaImportFolders.ListFiles(folder, query.CaseFromFirstFolder, cancellationToken);
            using var output = new MemoryStream();
            await ImportManifest.WriteAsync(output, ToAsync(rows), cancellationToken);
            var name = "опись-" + (Path.GetFileName(folder) is { Length: > 0 } leaf ? leaf : "архив") + ".csv";
            return ResponseDto<MediaImportFile>.Ok(new MediaImportFile(output.ToArray(), name, rows.Count, truncated));
        }
    }

    private static async IAsyncEnumerable<ManifestRow> ToAsync(IEnumerable<ManifestRow> rows)
    {
        foreach (var row in rows)
        {
            yield return row;
        }

        await Task.CompletedTask;
    }
}

/// <summary>
/// Проверка описи до запуска (ADR-0041 п. 3): ничего не записывает. Итог содержит отпечаток — с ним запускается
/// задание.
/// </summary>
/// <param name="Root">Каталог из настройки.</param>
/// <param name="Folder">Вложенная папка архива.</param>
/// <param name="ManifestFileName">Имя файла описи.</param>
/// <param name="ManifestContent">Содержимое описи.</param>
/// <param name="DocumentTypeId">Тип для документов, у которых в описи тип не указан (ADR-0042).</param>
public sealed record CheckMediaImportQuery(
    string Root, string? Folder, string ManifestFileName, byte[] ManifestContent, int? DocumentTypeId = null)
    : IRequest<ResponseDto<MediaImportCheck>>, IAuditableRequest
{
    /// <summary>Сколько строк проверено — для полосы хода на экране (необязательно).</summary>
    public IProgress<int>? Progress { get; init; }

    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.View;

    /// <inheritdoc />
    public string? AuditSummary => $"media:import:check;root={Root};folder={Folder};manifest={ManifestFileName}";

    /// <inheritdoc cref="CheckMediaImportQuery" />
    public sealed class Handler(
        IMediaAdministration administration,
        IAccessContextProvider accessProvider,
        MediaImportOptions options,
        MediaImportPlanner planner,
        TimeProvider time)
        : IRequestHandler<CheckMediaImportQuery, ResponseDto<MediaImportCheck>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<MediaImportCheck>> Handle(CheckMediaImportQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);
            if (!await administration.CanImportArchiveAsync(cancellationToken))
            {
                return ResponseDto<MediaImportCheck>.BadRequest(MediaImportRules.NoPermissionMessage);
            }

            if (query.ManifestContent is not { Length: > 0 })
            {
                return ResponseDto<MediaImportCheck>.BadRequest(MediaImportRules.NoManifestMessage);
            }

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var folder = new MediaImportPaths().ResolveFolder(options.UsableRoots, query.Root, query.Folder, out var problem);
            if (folder is null)
            {
                return ResponseDto<MediaImportCheck>.BadRequest(problem ?? "Папка недоступна.");
            }

            var manifest = ImportManifest.Read(query.ManifestContent);
            if (manifest.Error is not null)
            {
                return ResponseDto<MediaImportCheck>.Ok(new MediaImportCheck(manifest, null), manifest.Error);
            }

            var plan = await planner.PlanAsync(
                folder, query.ManifestFileName, manifest, access, time.LocalTimeZone, time.GetUtcNow(), query.DocumentTypeId,
                query.Progress, cancellationToken);
            return ResponseDto<MediaImportCheck>.Ok(new MediaImportCheck(manifest, plan));
        }
    }
}

/// <summary>
/// Запуск задания импорта (ADR-0041 п. 4–5): проверка повторяется, и задание создаётся, только если её итог совпал с
/// подтверждённым оператором (<paramref name="Fingerprint"/>). Файлы примет фоновая служба от имени запустившего.
/// </summary>
/// <param name="Root">Каталог из настройки.</param>
/// <param name="Folder">Вложенная папка архива.</param>
/// <param name="ManifestFileName">Имя описи.</param>
/// <param name="ManifestContent">Содержимое описи.</param>
/// <param name="Fingerprint">Отпечаток итога проверки, который видел оператор.</param>
/// <param name="Confirmed">Оператор подтвердил гриф и подразделение каждого дела (ТБ-024).</param>
/// <param name="DocumentTypeId">Тип для документов, у которых в описи тип не указан (ADR-0042).</param>
public sealed record StartMediaImportCommand(
    string Root, string? Folder, string ManifestFileName, byte[] ManifestContent, string Fingerprint, bool Confirmed,
    int? DocumentTypeId = null)
    : IRequest<ResponseDto<int>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Ingest;

    /// <inheritdoc />
    public string? AuditSummary => $"media:import:start;root={Root};folder={Folder};manifest={ManifestFileName}";

    /// <inheritdoc cref="StartMediaImportCommand" />
    public sealed class Handler(
        IMediaAdministration administration,
        IAccessContextProvider accessProvider,
        MediaImportOptions options,
        MediaImportPlanner planner,
        IMediaImportStore imports,
        IAuditWriter audit,
        TimeProvider time)
        : IRequestHandler<StartMediaImportCommand, ResponseDto<int>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<int>> Handle(StartMediaImportCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);
            if (!await administration.CanImportArchiveAsync(cancellationToken))
            {
                return ResponseDto<int>.BadRequest(MediaImportRules.NoPermissionMessage);
            }

            if (!command.Confirmed)
            {
                return ResponseDto<int>.BadRequest(MediaImportRules.NotConfirmedMessage);
            }

            if (command.ManifestContent is not { Length: > 0 })
            {
                return ResponseDto<int>.BadRequest(MediaImportRules.NoManifestMessage);
            }

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            if (access.NumericSubjectId is not { } userId)
            {
                return ResponseDto<int>.BadRequest(MediaImportRules.NoUserMessage);
            }

            var folder = new MediaImportPaths().ResolveFolder(options.UsableRoots, command.Root, command.Folder, out var problem);
            if (folder is null)
            {
                return ResponseDto<int>.BadRequest(problem ?? "Папка недоступна.");
            }

            var manifest = ImportManifest.Read(command.ManifestContent);
            if (manifest.Error is not null)
            {
                return ResponseDto<int>.BadRequest(manifest.Error);
            }

            // ТБ-024: задание создаётся только из того итога, который оператор видел и подтвердил.
            var plan = await planner.PlanAsync(
                folder, command.ManifestFileName, manifest, access, time.LocalTimeZone, time.GetUtcNow(), command.DocumentTypeId,
                null, cancellationToken);
            if (!string.Equals(plan.Fingerprint, command.Fingerprint, StringComparison.Ordinal))
            {
                return ResponseDto<int>.BadRequest(MediaImportRules.PlanChangedMessage);
            }

            if (plan.Items.Count == 0)
            {
                return ResponseDto<int>.BadRequest(MediaImportRules.NothingToImportMessage);
            }

            var jobId = await imports.CreateJobAsync(
                new MediaImportJobDraft(folder, command.ManifestFileName, plan.ManifestSha256, userId, plan.Items),
                cancellationToken);

            await audit.WriteAsync(
                new AuditEntry(
                    AuditAction.Ingest,
                    Math.Max(access.MaxClassification, plan.Items.Max(i => i.Classification)),
                    userId,
                    ObjectRef: "media:import:job=" + jobId.ToString(CultureInfo.InvariantCulture) + ":start",
                    PayloadSensitive: $"импорт архива запущен: папка «{folder}»; опись «{command.ManifestFileName}» "
                        + $"sha256={plan.ManifestSha256}; строк к приёму {plan.Items.Count} (из них документов {plan.DocumentCount}), "
                        + $"объём {MediaFileRules.DescribeSize(plan.TotalBytes)}; "
                        + $"дел {plan.Cases.Count} (№ {string.Join(", ", plan.Cases.Select(c => c.Target.Case.Number))}); "
                        + $"строк с ошибками на проверке {plan.Problems.Count}; гриф и подразделение дел подтверждены оператором"),
                cancellationToken);

            return ResponseDto<int>.Ok(jobId, $"Задание {jobId} запущено: {plan.Items.Count} файлов.");
        }
    }
}

/// <summary>Остановить задание импорта (продолжается потом с того же места).</summary>
/// <param name="JobId">Задание.</param>
public sealed record StopMediaImportCommand(int JobId) : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => "media:import:stop:job=" + JobId.ToString(CultureInfo.InvariantCulture);

    /// <inheritdoc cref="StopMediaImportCommand" />
    public sealed class Handler(IMediaAdministration administration, ISubjectProvider subjects, IMediaImportStore imports)
        : IRequestHandler<StopMediaImportCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(StopMediaImportCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);
            if (!await administration.CanImportArchiveAsync(cancellationToken))
            {
                return ResponseDto<bool>.BadRequest(MediaImportRules.NoPermissionMessage);
            }

            var who = await subjects.GetCurrentUserIdAsync(cancellationToken);
            var stopped = await imports.StopAsync(
                command.JobId,
                "Остановлено оператором" + (who is { } id ? " (пользователь " + id.ToString(CultureInfo.InvariantCulture) + ")" : string.Empty) + ".",
                cancellationToken);
            return stopped
                ? ResponseDto<bool>.Ok(true, "Задание остановлено. Файл, который принимался, будет принят заново при продолжении.")
                : ResponseDto<bool>.BadRequest("Задание не идёт и не ждёт очереди — останавливать нечего.");
        }
    }
}

/// <summary>
/// Продолжить остановленное задание. Файлы по-прежнему принимаются от имени того, кто задание запустил, и под его
/// допуском: подтверждение грифа и подразделения было его.
/// </summary>
/// <param name="JobId">Задание.</param>
public sealed record ResumeMediaImportCommand(int JobId) : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => "media:import:resume:job=" + JobId.ToString(CultureInfo.InvariantCulture);

    /// <inheritdoc cref="ResumeMediaImportCommand" />
    public sealed class Handler(IMediaAdministration administration, IMediaImportStore imports)
        : IRequestHandler<ResumeMediaImportCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(ResumeMediaImportCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);
            if (!await administration.CanImportArchiveAsync(cancellationToken))
            {
                return ResponseDto<bool>.BadRequest(MediaImportRules.NoPermissionMessage);
            }

            return await imports.ResumeAsync(command.JobId, cancellationToken)
                ? ResponseDto<bool>.Ok(true, "Задание продолжится в порядке очереди.")
                : ResponseDto<bool>.BadRequest("Задание не остановлено — продолжать нечего.");
        }
    }
}

/// <summary>Последние задания импорта.</summary>
public sealed record ListMediaImportJobsQuery : IRequest<ResponseDto<IReadOnlyList<MediaImportJobSummary>>>
{
    /// <summary>Сколько заданий показать.</summary>
    public const int Take = 50;

    /// <inheritdoc cref="ListMediaImportJobsQuery" />
    public sealed class Handler(IMediaAdministration administration, IMediaImportStore imports)
        : IRequestHandler<ListMediaImportJobsQuery, ResponseDto<IReadOnlyList<MediaImportJobSummary>>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<IReadOnlyList<MediaImportJobSummary>>> Handle(
            ListMediaImportJobsQuery query, CancellationToken cancellationToken)
        {
            if (!await administration.CanImportArchiveAsync(cancellationToken))
            {
                return ResponseDto<IReadOnlyList<MediaImportJobSummary>>.BadRequest(MediaImportRules.NoPermissionMessage);
            }

            return ResponseDto<IReadOnlyList<MediaImportJobSummary>>.Ok(await imports.ListJobsAsync(Take, cancellationToken));
        }
    }
}

/// <summary>Задание и страница его строк под решёткой допуска смотрящего (ТБ-020).</summary>
/// <param name="JobId">Задание.</param>
/// <param name="Status">Отбор строк по состоянию.</param>
/// <param name="Skip">Сколько строк пропустить.</param>
/// <param name="Take">Сколько строк выдать.</param>
public sealed record GetMediaImportJobQuery(int JobId, MediaImportItemStatus? Status = null, int Skip = 0, int Take = 50)
    : IRequest<ResponseDto<MediaImportJobDetails>>
{
    /// <inheritdoc cref="GetMediaImportJobQuery" />
    public sealed class Handler(IMediaAdministration administration, IAccessContextProvider accessProvider, IMediaImportStore imports)
        : IRequestHandler<GetMediaImportJobQuery, ResponseDto<MediaImportJobDetails>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<MediaImportJobDetails>> Handle(GetMediaImportJobQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);
            if (!await administration.CanImportArchiveAsync(cancellationToken))
            {
                return ResponseDto<MediaImportJobDetails>.BadRequest(MediaImportRules.NoPermissionMessage);
            }

            var job = await imports.GetJobAsync(query.JobId, cancellationToken);
            if (job is null)
            {
                return ResponseDto<MediaImportJobDetails>.NotFound("Задание не найдено.");
            }

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var items = await imports.ListItemsAsync(query.JobId, query.Status, access, query.Skip, query.Take, cancellationToken);
            return ResponseDto<MediaImportJobDetails>.Ok(new MediaImportJobDetails(job, items));
        }
    }
}

/// <summary>
/// Отчёт задания в CSV (ADR-0041 п. 8): все строки — с состоянием, причиной, носителем и хешами до и после переноса;
/// либо только отказы — в формате описи, чтобы исправить и загрузить новым заданием. Строки — под решёткой
/// смотрящего (ТБ-020); выгрузка — в журнал.
/// </summary>
/// <param name="JobId">Задание.</param>
/// <param name="RejectedAsManifest">Только отказы, в формате описи.</param>
public sealed record ExportMediaImportReportQuery(int JobId, bool RejectedAsManifest)
    : IRequest<ResponseDto<MediaImportFile>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Export;

    /// <inheritdoc />
    public string? AuditSummary =>
        "media:import:report:job=" + JobId.ToString(CultureInfo.InvariantCulture) + (RejectedAsManifest ? ";rejected-as-manifest" : string.Empty);

    /// <inheritdoc cref="ExportMediaImportReportQuery" />
    public sealed class Handler(
        IMediaAdministration administration, IAccessContextProvider accessProvider, IMediaImportStore imports, TimeProvider time)
        : IRequestHandler<ExportMediaImportReportQuery, ResponseDto<MediaImportFile>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<MediaImportFile>> Handle(ExportMediaImportReportQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);
            if (!await administration.CanImportArchiveAsync(cancellationToken))
            {
                return ResponseDto<MediaImportFile>.BadRequest(MediaImportRules.NoPermissionMessage);
            }

            if (await imports.GetJobAsync(query.JobId, cancellationToken) is null)
            {
                return ResponseDto<MediaImportFile>.NotFound("Задание не найдено.");
            }

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var zone = time.LocalTimeZone;
            using var output = new MemoryStream();
            var count = 0;
            var job = query.JobId.ToString(CultureInfo.InvariantCulture);
            if (query.RejectedAsManifest)
            {
                var rows = imports.StreamItemsAsync(query.JobId, MediaImportItemStatus.Rejected, access, cancellationToken);
                await ImportManifest.WriteAsync(output, AsManifest(rows, zone, () => count++, cancellationToken), cancellationToken);
                return ResponseDto<MediaImportFile>.Ok(new MediaImportFile(output.ToArray(), $"опись-отказы-задание-{job}.csv", count));
            }

            await MediaImportReport.WriteAsync(
                output, imports.StreamItemsAsync(query.JobId, null, access, cancellationToken), () => count++, cancellationToken);
            return ResponseDto<MediaImportFile>.Ok(new MediaImportFile(output.ToArray(), $"отчёт-импорта-задание-{job}.csv", count));
        }

        private static async IAsyncEnumerable<ManifestRow> AsManifest(
            IAsyncEnumerable<MediaImportItemView> items, TimeZoneInfo zone, Action counted,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var item in items.WithCancellation(cancellationToken))
            {
                counted();
                yield return new ManifestRow(
                    item.RowNumber,
                    item.RelativePath,
                    item.CaseNumber,
                    null,
                    item.CapturedAt is { } at ? MediaImportReport.FormatLocal(at, zone) : null,
                    item.Source,
                    item.Place,
                    item.ExpectedSha256);
            }
        }
    }
}
