using System.Globalization;
using System.Runtime.CompilerServices;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.BackgroundTasks;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Application;
using ISC.AI.Modules.Media.Application.Features.Assets;
using ISC.AI.Modules.Media.Domain.Services;
using ISC.AI.Profile.Investigation.Application.Features.Cases;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;

namespace ISC.AI.Profile.Investigation.Application.Features.AccessImport;

/// <summary>
/// Перенос базы Access в рабочие поля (ADR-0043): задание → дело вида «задание по объекту» с реквизитами, объект →
/// фигурант с анкетой, фото → материалы дела, связи → фигуранты-связи с автомобилями и адресами посещения.
/// </summary>
/// <remarks>
/// <para>
/// ТЕ ЖЕ ПУТИ, ЧТО РУЧНОЙ ВВОД. Дела, фигуранты, адреса и автомобили пишутся хранилищами под допуском оператора
/// (решётка и проверка грифа/подразделения — ТБ-020/024), фото — общим приёмом пакета «Медиа» (проверка формата по
/// содержимому, гриф дела, дедупликация — ТС-010, ТФ-МЕД-04). Права и допуск проверяет вызывающий сценарий.
/// </para>
/// <para>
/// ПОВТОР БЕЗ ДУБЛЕЙ (п. 6). Порядок записи по заданию: дело → соответствие «запись → дело» → объект и фото →
/// соответствие «запись → дело + объект». Сбой между шагами повтор той же базы доводит: дело уже есть по
/// соответствию, объект ищется среди фигурантов дела, фото принимается заново и узнаётся дедупликацией. Связь
/// записывается целиком (фигурант, автомобиль, адреса) до своего соответствия: при сбое посередине повтор заведёт её
/// заново — лишняя карточка видна и удаляется, а потерянные сведения не видны никому.
/// </para>
/// <para>
/// ЖУРНАЛ (п. 8, ТБ-030): каждое задание — отдельная запись: ссылка на дело и итог в метаданных, код записи, № дела и
/// SHA-256 файла — в защищённой части (№ задания — режимное сведение, в метаданные журнала не идёт, ТБ-032).
/// </para>
/// </remarks>
public sealed class AccessImporter(
    IAccessImportRecordStore records,
    IReferenceStore references,
    ICaseStore cases,
    IPersonStore persons,
    IPersonRequisiteStore requisites,
    ICaseScope caseScope,
    IMediaStore mediaStore,
    MediaUploadOptions uploadOptions,
    IBackgroundTaskQueue queue,
    IAuditWriter audit,
    TimeProvider time)
{
    /// <summary>Запись справочника ГУ для заданий без инициатора (ADR-0043 п. 5).</summary>
    public const string PlaceholderInitiator = "Не указан (перенос из Access)";

    /// <summary>Заменитель пустого обязательного реквизита задания — обоснования, цели (ADR-0043 п. 5).</summary>
    public const string NotSpecified = AccessSnapshot.NotSpecified;

    /// <summary>Отказ: база изменилась между двумя чтениями.</summary>
    public const string ChangedDuringReadMessage =
        "Строки таблицы заданий при повторном чтении не совпали — база изменилась во время переноса. Загрузите её заново.";

    /// <summary>Примечание к адресу посещения.</summary>
    public const string VisitAddressNote = "адрес посещения (перенос из Access)";

    private const int SampleSize = 10;

    /// <summary>Проверка до переноса — без записи (ADR-0043 п. 7).</summary>
    /// <param name="database">Открытая база.</param>
    /// <param name="file">Загруженный файл.</param>
    /// <param name="mapping">Сопоставление.</param>
    /// <param name="options">Выбор оператора.</param>
    /// <param name="cancellationToken">Отмена.</param>
    /// <exception cref="AccessDatabaseReadException">Сопоставление не годится или база не читается.</exception>
    public async Task<AccessImportPlan> PlanAsync(
        IAccessDatabase database, AccessDatabaseFile file, AccessMapping mapping, AccessImportOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(options);

        var snapshot = await LoadAsync(database, file, mapping, cancellationToken);
        var done = await records.ListAsync(file.Sha256, cancellationToken);
        var catalog = await ReferenceCatalog.LoadAsync(references, cancellationToken);
        var (initiatorsToCreate, linkTypesToCreate) = ApplyReferenceRules(snapshot, mapping, done, catalog, options.CreateMissingReferences);
        MarkLinksOfFailedTasks(snapshot);

        var newTasks = 0;
        var alreadyImported = 0;
        var photos = 0;
        var sample = new List<AccessTaskPreview>();
        var planWarnings = new List<AccessImportProblem>();
        await foreach (var (task, blob) in WithPhotosAsync(database, mapping, snapshot, cancellationToken))
        {
            if (task.Problem is not null)
            {
                continue;
            }

            if (IsDone(done, mapping.TasksTable!, task))
            {
                alreadyImported++;
                continue;
            }

            newTasks++;
            planWarnings.AddRange(task.Warnings.Select(w => new AccessImportProblem(mapping.TasksTable!, task.Key, "задание перенесётся, но " + w)));
            if (blob is not null)
            {
                if (AccessPhotoExtractor.TryExtract(blob) is null)
                {
                    planWarnings.Add(new AccessImportProblem(mapping.TasksTable!, task.Key, "задание перенесётся, но " + AccessPhotoExtractor.UnrecognizedMessage));
                }
                else
                {
                    photos++;
                }
            }

            if (sample.Count < SampleSize)
            {
                sample.Add(new AccessTaskPreview(
                    task.Key, task.CaseNumber, task.DisplayName ?? string.Empty, task.Date,
                    task.Links.Count(l => l.Problem is null), task.Initiator));
            }
        }

        var linksToImport = mapping.LinksTable is null
            ? []
            : snapshot.Links.Where(l => l.Problem is null && !done.ContainsKey((mapping.LinksTable, l.Key))).ToList();
        planWarnings.AddRange(linksToImport.SelectMany(l =>
            l.Warnings.Select(w => new AccessImportProblem(mapping.LinksTable!, l.Key, "связь перенесётся, но " + w))));

        return new AccessImportPlan(
            snapshot.Tasks.Count,
            newTasks,
            alreadyImported,
            snapshot.Links.Count,
            linksToImport.Count,
            photos,
            initiatorsToCreate,
            linkTypesToCreate,
            [.. CollectProblems(snapshot, mapping), .. planWarnings],
            sample,
            AccessSnapshot.Fingerprint(file.Sha256, mapping, options));
    }

    /// <summary>Перенос. Возвращает итог; ошибки по записям — в отчёте, перенос остальных продолжается.</summary>
    /// <param name="database">Открытая база.</param>
    /// <param name="file">Загруженный файл.</param>
    /// <param name="mapping">Сопоставление.</param>
    /// <param name="options">Выбор оператора (подтверждён вызывающим).</param>
    /// <param name="access">Допуск оператора.</param>
    /// <param name="operatorUserId">Оператор (автор записей).</param>
    /// <param name="progress">Сколько заданий обработано.</param>
    /// <param name="cancellationToken">Отмена: перенесённое остаётся, повтор продолжит с места остановки.</param>
    /// <exception cref="AccessDatabaseReadException">Сопоставление не годится или база не читается.</exception>
    public async Task<AccessImportReport> RunAsync(
        IAccessDatabase database,
        AccessDatabaseFile file,
        AccessMapping mapping,
        AccessImportOptions options,
        AccessContext access,
        int operatorUserId,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(access);

        var snapshot = await LoadAsync(database, file, mapping, cancellationToken);
        var done = await records.ListAsync(file.Sha256, cancellationToken);
        var catalog = await ReferenceCatalog.LoadAsync(references, cancellationToken);
        var run = new RunContext(file, mapping, options, access, operatorUserId, catalog, done, Today());

        if (options.CreateMissingReferences)
        {
            var (initiators, linkTypes) = ApplyReferenceRules(snapshot, mapping, done, catalog, createMissing: true);
            foreach (var name in initiators)
            {
                await CreateReferenceAsync(run, ReferenceKind.InitiatorUnit, name, cancellationToken);
            }

            foreach (var name in linkTypes)
            {
                await CreateReferenceAsync(run, ReferenceKind.LinkType, name, cancellationToken);
            }
        }

        // Повторная сверка после заведения: запись, которую завести не удалось (выключена), — отказ по строкам.
        ApplyReferenceRules(snapshot, mapping, done, catalog, createMissing: false);

        var processed = 0;
        var warnings = new List<AccessImportProblem>();
        try
        {
            await foreach (var (task, blob) in WithPhotosAsync(database, mapping, snapshot, cancellationToken))
            {
                progress?.Report(++processed);
                if (task.Problem is null)
                {
                    await ImportTaskAsync(run, task, blob, warnings, cancellationToken);
                }
            }
        }
        catch (StopImportException stop)
        {
            return run.Report([new AccessImportProblem(mapping.TasksTable!, stop.Key, stop.Message), .. CollectProblems(snapshot, mapping), .. warnings], stopped: true);
        }

        return run.Report([.. CollectProblems(snapshot, mapping), .. warnings], stopped: false);
    }

    private async Task<AccessSnapshot> LoadAsync(
        IAccessDatabase database, AccessDatabaseFile file, AccessMapping mapping, CancellationToken cancellationToken)
    {
        if (AccessSnapshot.Validate(database.Tables, mapping) is { } error)
        {
            throw new AccessDatabaseReadException(error);
        }

        return await AccessSnapshot.LoadAsync(database, mapping, file.FileName, Today(), cancellationToken);
    }

    private DateOnly Today() =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), time.LocalTimeZone).DateTime);

    private static bool IsDone(IReadOnlyDictionary<(string Table, string Key), AccessImportRecord> done, string table, AccessTaskData task) =>
        done.TryGetValue((table, task.Key), out var record) && record.PersonId is not null;

    private static List<AccessImportProblem> CollectProblems(AccessSnapshot snapshot, AccessMapping mapping)
    {
        MarkLinksOfFailedTasks(snapshot);
        return
        [
            .. snapshot.Tasks.Where(t => t.Problem is not null).Select(t => new AccessImportProblem(mapping.TasksTable!, t.Key, t.Problem!)),
            .. snapshot.Links.Where(l => l.Problem is not null).Select(l => new AccessImportProblem(mapping.LinksTable!, l.Key, l.Problem!)),
        ];
    }

    /// <summary>Связи задания, которое не переносится, — тоже в отчёт, а не молча мимо.</summary>
    private static void MarkLinksOfFailedTasks(AccessSnapshot snapshot)
    {
        foreach (var task in snapshot.Tasks.Where(t => t.Problem is not null))
        {
            foreach (var link in task.Links.Where(l => l.Problem is null))
            {
                link.Problem = $"задание «{task.CaseNumber}» не переносится — связь тоже";
            }
        }
    }

    /// <summary>
    /// Сверка значений справочников: каких записей нет (их заведут по галочке) и какие строки без галочки не
    /// переносятся. Сравнение — без регистра, «ё» и лишних пробелов; выключенная запись не используется и не
    /// заводится повторно — строка с ней в отчёт.
    /// </summary>
    private static (List<string> Initiators, List<string> LinkTypes) ApplyReferenceRules(
        AccessSnapshot snapshot,
        AccessMapping mapping,
        IReadOnlyDictionary<(string Table, string Key), AccessImportRecord> done,
        ReferenceCatalog catalog,
        bool createMissing)
    {
        var initiators = new List<string>();
        var linkTypes = new List<string>();
        var seenInitiators = new HashSet<string>(StringComparer.Ordinal);
        var seenLinkTypes = new HashSet<string>(StringComparer.Ordinal);

        // Инициатор нужен только для заведения дела: у задания, дело которого уже заведено, он не сверяется.
        foreach (var task in snapshot.Tasks.Where(t => t.Problem is null && !done.ContainsKey((mapping.TasksTable!, t.Key))))
        {
            var name = task.Initiator ?? PlaceholderInitiator;
            if (catalog.Find(ReferenceKind.InitiatorUnit, name) is not null)
            {
                continue;
            }

            if (catalog.IsInactive(ReferenceKind.InitiatorUnit, name))
            {
                task.Problem = $"инициатор «{name}» в справочнике ГУ выключен — включите запись";
            }
            else if (!createMissing)
            {
                task.Problem = $"инициатора «{name}» нет в справочнике ГУ — заведите запись или отметьте «завести недостающие»";
            }
            else if (seenInitiators.Add(AccessValues.ReferenceKey(name)))
            {
                initiators.Add(name);
            }
        }

        foreach (var link in snapshot.Links.Where(l => l.Problem is null && l.LinkType is not null
            && !done.ContainsKey((mapping.LinksTable!, l.Key))))
        {
            var name = link.LinkType!;
            if (catalog.Find(ReferenceKind.LinkType, name) is not null)
            {
                continue;
            }

            if (catalog.IsInactive(ReferenceKind.LinkType, name))
            {
                link.Problem = $"тип связи «{name}» в справочнике выключен — включите запись";
            }
            else if (!createMissing)
            {
                link.Problem = $"типа связи «{name}» нет в справочнике — заведите запись или отметьте «завести недостающие»";
            }
            else if (seenLinkTypes.Add(AccessValues.ReferenceKey(name)))
            {
                linkTypes.Add(name);
            }
        }

        return (initiators, linkTypes);
    }

    /// <summary>
    /// Задания вместе с полем фото — вторым чтением в том же порядке, по одной строке: снимки тысяч записей в память
    /// разом не берутся. Строки сверяются с первым чтением по коду (№) записи.
    /// </summary>
    private static async IAsyncEnumerable<(AccessTaskData Task, byte[]? Photo)> WithPhotosAsync(
        IAccessDatabase database, AccessMapping mapping, AccessSnapshot snapshot,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!mapping.TaskColumns.TryGetValue(AccessField.Photo, out var photoColumn))
        {
            foreach (var task in snapshot.Tasks)
            {
                yield return (task, null);
            }

            yield break;
        }

        var byKey = mapping.TaskColumns.TryGetValue(AccessField.TaskKey, out var keyColumn);
        var checkColumn = byKey ? keyColumn : mapping.TaskColumns.GetValueOrDefault(AccessField.TaskNumber);
        string[] columns = checkColumn is null ? [photoColumn] : [checkColumn, photoColumn];
        var index = 0;
        await foreach (var row in database.ReadRowsAsync(mapping.TasksTable!, columns, AccessSnapshot.TaskOrder(mapping), cancellationToken))
        {
            if (index >= snapshot.Tasks.Count)
            {
                throw new AccessDatabaseReadException(ChangedDuringReadMessage);
            }

            var task = snapshot.Tasks[index++];
            if (checkColumn is not null
                && !string.Equals(AccessValues.Text(row.GetValueOrDefault(checkColumn)), byKey ? task.KeyText : task.Number, StringComparison.Ordinal))
            {
                throw new AccessDatabaseReadException(ChangedDuringReadMessage);
            }

            yield return (task, row.GetValueOrDefault(photoColumn) as byte[]);
        }

        if (index != snapshot.Tasks.Count)
        {
            throw new AccessDatabaseReadException(ChangedDuringReadMessage);
        }
    }

    private async Task ImportTaskAsync(
        RunContext run, AccessTaskData task, byte[]? photo, List<AccessImportProblem> warnings, CancellationToken cancellationToken)
    {
        var table = run.Mapping.TasksTable!;
        var record = run.Done.GetValueOrDefault((table, task.Key));
        var outcome = new List<string>();
        int caseId;
        if (record is null)
        {
            var initiatorId = run.Catalog.Find(ReferenceKind.InitiatorUnit, task.Initiator ?? PlaceholderInitiator)!.Value;
            var draft = new CaseDraft(
                task.CaseNumber,
                Title(task),
                CaseKind.ObjectTask,
                task.Date ?? run.Today,
                InvestigatorUserId: null,
                run.Options.DivisionId,
                run.Options.Classification,
                Basis: null,
                run.OperatorUserId,
                new TaskRequisites(task.CaseNumber, initiatorId, task.Justification, task.Purpose, Notes: task.TaskNotes));
            var (result, createdId) = await cases.CreateAsync(draft, run.Access, cancellationToken);
            switch (result)
            {
                case CaseWriteResult.Ok:
                    break;
                case CaseWriteResult.OutsideClearance:
                    // Гриф и подразделение у всей базы одни: дальше откажет каждая запись — перенос останавливается.
                    throw new StopImportException(task.Key, CaseGuard.OutsideClearance);
                case CaseWriteResult.DuplicateNumber:
                    task.Problem = $"дело № «{task.CaseNumber}» в подразделении уже есть — запись не перенесена (слияния нет)";
                    await WriteTaskAuditAsync(run, task, null, "отказ", cancellationToken);
                    return;
                case CaseWriteResult.InvalidTask:
                    task.Problem = "реквизиты задания не приняты: запись справочника ГУ выключена или удалена";
                    await WriteTaskAuditAsync(run, task, null, "отказ", cancellationToken);
                    return;
                default:
                    task.Problem = "дело не заведено";
                    await WriteTaskAuditAsync(run, task, null, "отказ", cancellationToken);
                    return;
            }

            caseId = createdId;
            run.CasesCreated++;
            outcome.Add("дело заведено");
            warnings.AddRange(task.Warnings.Select(w => new AccessImportProblem(table, task.Key, "задание перенесено, но " + w)));
            record = new AccessImportRecord(run.File.Sha256, table, task.Key, caseId, null);
            await records.SaveAsync(record, cancellationToken);

            if (run.Options.Status == CaseStatus.Suspended)
            {
                await cases.SetStatusAsync(caseId, CaseStatus.Suspended, run.Access, cancellationToken);
            }
        }
        else
        {
            caseId = record.CaseId;
        }

        var objectId = record.PersonId;
        if (objectId is null)
        {
            // Возобновление: объект мог быть заведён до сбоя — он единственный фигурант-объект этого дела.
            var existing = (await persons.ListByCaseAsync(caseId, run.Access, cancellationToken))
                .FirstOrDefault(p => p.Role == PersonRole.Target);
            if (existing is not null)
            {
                objectId = existing.Id;
            }
            else
            {
                var (result, personId) = await persons.CreateAsync(
                    new PersonDraft(caseId, task.DisplayName, false, null, task.PersonNotes, PersonRole.Target, task.Questionnaire),
                    run.Access,
                    cancellationToken);
                if (result != PersonWriteResult.Ok)
                {
                    task.Problem = "дело заведено, но объект не заведён — повторите перенос";
                    await WriteTaskAuditAsync(run, task, caseId, "отказ", cancellationToken);
                    return;
                }

                objectId = personId;
                run.ObjectsCreated++;
                outcome.Add("объект заведён");
            }

            if (photo is not null)
            {
                var photoOutcome = await ImportPhotoAsync(run, task, caseId, photo, cancellationToken);
                if (photoOutcome is null)
                {
                    outcome.Add("фото принято");
                }
                else
                {
                    warnings.Add(new AccessImportProblem(table, task.Key, "задание перенесено, но " + photoOutcome));
                    outcome.Add("фото: " + photoOutcome);
                }
            }

            await records.SaveAsync(new AccessImportRecord(run.File.Sha256, table, task.Key, caseId, objectId), cancellationToken);
        }
        else
        {
            run.AlreadyImported++;
        }

        var links = 0;
        foreach (var link in task.Links.Where(l => l.Problem is null && !run.Done.ContainsKey((run.Mapping.LinksTable!, l.Key))))
        {
            if (await ImportLinkAsync(run, link, caseId, objectId.Value, cancellationToken))
            {
                links++;
                warnings.AddRange(link.Warnings.Select(w => new AccessImportProblem(run.Mapping.LinksTable!, link.Key, "связь перенесена, но " + w)));
            }
        }

        if (links > 0)
        {
            outcome.Add($"связей: {links.ToString(CultureInfo.InvariantCulture)}");
        }

        if (outcome.Count > 0)
        {
            await WriteTaskAuditAsync(run, task, caseId, string.Join("; ", outcome), cancellationToken);
        }
    }

    /// <summary>Фото объекта в материалы дела; <see langword="null"/> — принято, иначе причина для отчёта.</summary>
    private async Task<string?> ImportPhotoAsync(
        RunContext run, AccessTaskData task, int caseId, byte[] blob, CancellationToken cancellationToken)
    {
        var photo = AccessPhotoExtractor.TryExtract(blob);
        if (photo is null)
        {
            return AccessPhotoExtractor.UnrecognizedMessage;
        }

        var target = await caseScope.GetCaseAsync(caseId, run.Access, cancellationToken);
        if (target is null)
        {
            return "фото не принято: дело недоступно";
        }

        // Дата съёмки — из метаданных снимка; её нет (скан, фото с документа) — дата переноса с пометкой в источнике,
        // чтобы её не приняли за настоящую дату съёмки (ADR-0043 п. 3, ТФ-МЕД-17).
        var now = time.GetUtcNow();
        var head = photo.Content.AsSpan(0, Math.Min(photo.Content.Length, CaptureTimeProbe.HeadBytes));
        var captured = CaptureTimeProbe.TryRead(head, time.LocalTimeZone, now);
        var source = $"Перенос из базы Access «{run.File.FileName}», запись {task.Key}"
            + (captured is null ? "; дата съёмки неизвестна — указана дата переноса" : string.Empty);

        var intake = new MediaIntake(mediaStore, caseScope, uploadOptions);
        var result = await intake.ReceiveAsync(
            new MediaIntakeRequest(
                target,
                "access-" + SafeName(task.Key) + photo.Extension,
                photo.ContentType,
                photo.Content.Length,
                AccessValues.Fit(source, 500),
                captured ?? now,
                Place: null,
                run.OperatorUserId),
            new MemoryStream(photo.Content, writable: false),
            cancellationToken);
        if (!result.Accepted)
        {
            return "фото не принято: " + (result.Message ?? "отказ приёма");
        }

        run.PhotosAccepted++;
        if (!result.Receipt!.Duplicate)
        {
            // Обработка (поиск лиц) — тем же путём, что у загрузки: эталоном снимок делает сотрудник.
            await MediaProcessing.EnqueueAsync(queue, result.Receipt.AssetId, result.Kind!.Value, cancellationToken);
        }

        return null;
    }

    private async Task<bool> ImportLinkAsync(RunContext run, AccessLinkData link, int caseId, int objectId, CancellationToken cancellationToken)
    {
        var linkTypeId = link.LinkType is null ? null : run.Catalog.Find(ReferenceKind.LinkType, link.LinkType);
        var (result, personId) = await persons.CreateAsync(
            new PersonDraft(caseId, link.Name, link.Name is null, null, link.Notes, PersonRole.Link, null, objectId, linkTypeId),
            run.Access,
            cancellationToken);
        if (result != PersonWriteResult.Ok)
        {
            link.Problem = result == PersonWriteResult.InvalidLink
                ? "связь не принята: тип связи выключен или не подходит"
                : "связь не заведена";
            return false;
        }

        if (link.CarPlate is not null || link.CarMake is not null)
        {
            var (vehicle, _) = await requisites.SaveVehicleAsync(
                personId, null, new PersonVehicleDraft(link.CarPlate, link.CarMake, null, null, link.CarNotes), run.Access, cancellationToken);
            if (vehicle == PersonWriteResult.Ok)
            {
                run.VehiclesSaved++;
            }
        }

        foreach (var address in link.Addresses)
        {
            var (saved, _) = await requisites.SaveAddressAsync(
                personId, null, new PersonAddressDraft(AddressKind.Other, address, VisitAddressNote), run.Access, cancellationToken);
            if (saved == PersonWriteResult.Ok)
            {
                run.AddressesSaved++;
            }
        }

        await records.SaveAsync(new AccessImportRecord(run.File.Sha256, run.Mapping.LinksTable!, link.Key, caseId, personId), cancellationToken);
        run.LinksCreated++;
        return true;
    }

    private async Task CreateReferenceAsync(RunContext run, ReferenceKind kind, string name, CancellationToken cancellationToken)
    {
        var (result, id) = await references.CreateAsync(kind, name, null, ReferenceCatalog.ImportedSortOrder, cancellationToken);
        if (result != ReferenceWriteResult.Ok)
        {
            // Гонка с ручным заведением — запись уже есть; следующая сверка её найдёт.
            run.Catalog.Reload(await references.ListAsync(null, cancellationToken));
            return;
        }

        run.Catalog.Add(kind, name, id);
        run.ReferencesCreated++;
        await audit.WriteAsync(
            new AuditEntry(
                AuditAction.Modify,
                run.Access.MaxClassification,
                run.OperatorUserId,
                string.Create(CultureInfo.InvariantCulture, $"investigation:reference:{id}:create:kind={kind};source=access-import"),
                PayloadSensitive: $"перенос из Access: заведена запись справочника «{name}»"),
            cancellationToken);
    }

    private Task WriteTaskAuditAsync(RunContext run, AccessTaskData task, int? caseId, string what, CancellationToken cancellationToken)
    {
        var result = task.Problem is null ? "перенесено" : "отказ";
        return audit.WriteAsync(
            new AuditEntry(
                AuditAction.Ingest,
                Math.Max(run.Access.MaxClassification, run.Options.Classification),
                run.OperatorUserId,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"investigation:access-import:sha={run.File.Sha256[..16]};case={caseId?.ToString(CultureInfo.InvariantCulture) ?? "-"};result={result}"),
                run.Options.DivisionId,
                PayloadSensitive: $"перенос из Access: файл «{run.File.FileName}» (sha256={run.File.Sha256}); задание, код записи {task.Key}; "
                    + $"дело № {task.CaseNumber}; {what}" + (task.Problem is null ? string.Empty : "; причина: " + task.Problem)),
            cancellationToken);
    }

    private static string Title(AccessTaskData task) =>
        AccessValues.Fit($"Задание № {task.CaseNumber} — {task.DisplayName}", CreateCaseValidator.MaxTitleLength);

    private static string SafeName(string key)
    {
        var safe = new string(key.Where(char.IsLetterOrDigit).Take(40).ToArray());
        return safe.Length == 0 ? "photo" : safe;
    }

    /// <summary>Перенос остановлен целиком (допуск): дальше откажет каждая запись.</summary>
    private sealed class StopImportException(string key, string message) : Exception(message)
    {
        public string Key { get; } = key;
    }

    /// <summary>Состояние одного прогона и счётчики итога.</summary>
    private sealed class RunContext(
        AccessDatabaseFile file,
        AccessMapping mapping,
        AccessImportOptions options,
        AccessContext access,
        int operatorUserId,
        ReferenceCatalog catalog,
        IReadOnlyDictionary<(string Table, string Key), AccessImportRecord> done,
        DateOnly today)
    {
        public AccessDatabaseFile File => file;

        public AccessMapping Mapping => mapping;

        public AccessImportOptions Options => options;

        public AccessContext Access => access;

        public int OperatorUserId => operatorUserId;

        public ReferenceCatalog Catalog => catalog;

        public IReadOnlyDictionary<(string Table, string Key), AccessImportRecord> Done => done;

        public DateOnly Today => today;

        public int CasesCreated { get; set; }

        public int ObjectsCreated { get; set; }

        public int LinksCreated { get; set; }

        public int PhotosAccepted { get; set; }

        public int VehiclesSaved { get; set; }

        public int AddressesSaved { get; set; }

        public int ReferencesCreated { get; set; }

        public int AlreadyImported { get; set; }

        public AccessImportReport Report(IReadOnlyList<AccessImportProblem> problems, bool stopped) =>
            new(CasesCreated, ObjectsCreated, LinksCreated, PhotosAccepted, VehiclesSaved, AddressesSaved, ReferencesCreated,
                AlreadyImported, problems, stopped);
    }

    /// <summary>Записи справочников ГУ и типов связей по нормализованному наименованию.</summary>
    private sealed class ReferenceCatalog
    {
        /// <summary>Порядок заведённых переносом записей — после заведённых вручную.</summary>
        public const int ImportedSortOrder = 1000;

        private readonly Dictionary<(ReferenceKind, string), int> _active = [];
        private readonly HashSet<(ReferenceKind, string)> _inactive = [];

        public static async Task<ReferenceCatalog> LoadAsync(IReferenceStore store, CancellationToken cancellationToken)
        {
            var catalog = new ReferenceCatalog();
            catalog.Reload(await store.ListAsync(null, cancellationToken));
            return catalog;
        }

        public void Reload(IReadOnlyList<ReferenceItemRow> rows)
        {
            _active.Clear();
            _inactive.Clear();
            foreach (var row in rows.Where(r => r.Kind is ReferenceKind.InitiatorUnit or ReferenceKind.LinkType))
            {
                var key = (row.Kind, AccessValues.ReferenceKey(row.Name));
                if (row.IsActive)
                {
                    _active.TryAdd(key, row.Id);
                }
                else
                {
                    _inactive.Add(key);
                }
            }
        }

        public int? Find(ReferenceKind kind, string name) =>
            _active.TryGetValue((kind, AccessValues.ReferenceKey(name)), out var id) ? id : null;

        public bool IsInactive(ReferenceKind kind, string name) => _inactive.Contains((kind, AccessValues.ReferenceKey(name)));

        public void Add(ReferenceKind kind, string name, int id) => _active[(kind, AccessValues.ReferenceKey(name))] = id;
    }
}
