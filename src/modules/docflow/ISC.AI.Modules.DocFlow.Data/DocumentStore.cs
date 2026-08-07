using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Domain.Entities;
using ISC.AI.Modules.DocFlow.Domain.Enums;
using ISC.AI.Modules.DocFlow.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Modules.DocFlow.Data;

/// <summary>
/// Хранилище документов и назначений поверх <see cref="DocFlowDbContext"/> (ТЗ СКИД §3–4).
/// Контекст — на операцию, через фабрику (ТС-008, Blazor Server).
/// </summary>
public sealed class DocumentStore(
    IDbContextFactory<DocFlowDbContext> contextFactory,
    IDocFlowFileStorage fileStorage,
    IAccessPolicy accessPolicy,
    IUserDirectory users) : IDocumentStore
{
    // Перенос СКИД (UploadDocumentAttachmentCommandValidator, DL-057 / ТЗ §3.3.1).
    private const int MaxAttachmentsPerDocument = 10;

    /// <summary>
    /// Потолок размера страницы реестра. Не декоративный: без него запрос «дай 100000 строк» вернул
    /// бы весь корпус вместе с кратким содержанием каждого документа — то самое, ради ухода от чего
    /// постраничность и вводилась.
    /// </summary>
    public const int MaxPageSize = 200;

    /// <summary>
    /// Виден ли документ субъекту — ЕДИНСТВЕННОЕ место, где записан предикат доступа (решётка
    /// ТБ-020/021 + сужающая политика профиля ADR-0014). И чтение (<c>ListAsync</c>/<c>GetAsync</c>),
    /// и все проверки записи (<see cref="WriteAccessRule"/>) идут через него, чтобы правила не
    /// разъехались: раньше они и разъехались — чтение сузили, запись забыли (6.4.2).
    /// </summary>
    private IQueryable<Document> VisibleDocuments(DocFlowDbContext db, AccessContext access)
    {
        var allowedDivisions = access.AllowedDivisions;
        return db.Documents
            .Where(d => d.Classification <= access.MaxClassification
                && allowedDivisions.Contains(d.DivisionId))
            .Where(accessPolicy.BuildFilter<Document>(access));
    }

    /// <summary>Виден ли субъекту документ-владелец: недоступный неотличим от несуществующего.</summary>
    private Task<bool> IsDocumentVisibleAsync(
        DocFlowDbContext db, int documentId, AccessContext access, CancellationToken cancellationToken) =>
        VisibleDocuments(db, access).AnyAsync(d => d.Id == documentId, cancellationToken);

    /// <summary>Виден ли субъекту документ, которому принадлежит назначение (проверка перед записью).</summary>
    private async Task<bool> IsAssignmentVisibleAsync(
        DocFlowDbContext db, int assignmentId, AccessContext access, CancellationToken cancellationToken)
    {
        var documentId = await db.DocumentAssignments.AsNoTracking()
            .Where(a => a.Id == assignmentId)
            .Select(a => (int?)a.DocumentId)
            .FirstOrDefaultAsync(cancellationToken);

        return documentId is { } id && await IsDocumentVisibleAsync(db, id, access, cancellationToken);
    }


    /// <inheritdoc />
    public async Task<DocumentWriteStatus> AddDocumentFileAsync(
        int documentId, UploadedFile file, DocumentLanguage language, AccessContext access,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(access);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Нельзя менять то, чего не видишь (WriteAccessRule): недоступный документ — тот же NotFound.
        if (!await IsDocumentVisibleAsync(db, documentId, access, cancellationToken))
        {
            return DocumentWriteStatus.NotFound;
        }

        var uploadedByUserId = access.NumericSubjectId;

        // Содержимое — в хранилище ДО записи в БД; при сбое БД файл компенсирующе удаляется (как в СКИД).
        var subPath = documentId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var content = new MemoryStream(file.Content);
        var storedFileName = await fileStorage.SaveAsync(
            content, Path.GetExtension(file.FileName), FileCategories.Documents, subPath, cancellationToken);

        try
        {
            // §3.3: замена файла создаёт новую версию; прежняя того же языка теряет актуальность.
            var latest = await db.DocumentFiles.FirstOrDefaultAsync(
                f => f.DocumentId == documentId && f.Language == language && f.IsLatest, cancellationToken);
            if (latest is not null)
            {
                latest.IsLatest = false;
            }

            var maxVersion = await db.DocumentFiles
                .Where(f => f.DocumentId == documentId && f.Language == language)
                .Select(f => (int?)f.Version)
                .MaxAsync(cancellationToken) ?? 0;

            db.DocumentFiles.Add(new DocumentFile
            {
                DocumentId = documentId,
                Language = language,
                FileName = file.FileName,
                StoredFileName = storedFileName,
                ContentType = file.ContentType,
                FileSize = file.Content.LongLength,
                Version = maxVersion + 1,
                IsLatest = true,
                UploadedByUserId = uploadedByUserId ?? 0,
            });
            await db.SaveChangesAsync(cancellationToken);
            return DocumentWriteStatus.Ok;
        }
        catch
        {
            await fileStorage.DeleteAsync(storedFileName, FileCategories.Documents, subPath, CancellationToken.None);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<DocumentWriteStatus> AddAttachmentAsync(
        int documentId, UploadedFile file, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(access);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Нельзя менять то, чего не видишь (WriteAccessRule).
        if (!await IsDocumentVisibleAsync(db, documentId, access, cancellationToken))
        {
            return DocumentWriteStatus.NotFound;
        }

        var uploadedByUserId = access.NumericSubjectId;

        // Лимит — ДО записи в хранилище (не тратим байты на диске, если операция всё равно отклонится).
        var attachmentCount = await db.DocumentAttachments
            .CountAsync(a => a.DocumentId == documentId, cancellationToken);
        if (attachmentCount >= MaxAttachmentsPerDocument)
        {
            return DocumentWriteStatus.TooManyAttachments;
        }

        var subPath = documentId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var content = new MemoryStream(file.Content);
        var storedFileName = await fileStorage.SaveAsync(
            content, Path.GetExtension(file.FileName), FileCategories.Attachments, subPath, cancellationToken);

        try
        {
            db.DocumentAttachments.Add(new DocumentAttachment
            {
                DocumentId = documentId,
                FileName = file.FileName,
                StoredFileName = storedFileName,
                ContentType = file.ContentType,
                FileSize = file.Content.LongLength,
                UploadedByUserId = uploadedByUserId ?? 0,
            });
            await db.SaveChangesAsync(cancellationToken);
            return DocumentWriteStatus.Ok;
        }
        catch
        {
            await fileStorage.DeleteAsync(storedFileName, FileCategories.Attachments, subPath, CancellationToken.None);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<DocumentCreateResult> CreateAsync(
        DocumentDraft draft,
        IReadOnlyList<AssignmentDraft> assignments,
        bool useCommonDeadline,
        DateOnly? commonDeadline,
        AccessContext access,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(assignments);
        ArgumentNullException.ThrowIfNull(access);

        // Регистрация — единственная запись без «уже существующего» объекта, поэтому проверяется не
        // видимость, а вместимость в допуск автора (ТБ-020/021, этап 6.6): документ выше своего грифа
        // или в чужое подразделение создать нельзя — он тут же стал бы невидим самому создавшему.
        // Два РАЗНЫХ статуса, а не один общий: пользователь должен знать, какое поле исправлять.
        if (draft.Classification > access.MaxClassification)
        {
            return new DocumentCreateResult(DocumentWriteStatus.ClassificationOutsideClearance);
        }

        if (!access.AllowedDivisions.Contains(draft.DivisionId))
        {
            return new DocumentCreateResult(DocumentWriteStatus.DivisionOutsideClearance);
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Тип обязан существовать и быть действующим (§3.1: неактивный не предлагается при регистрации).
        var type = await db.DocumentTypes.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == draft.TypeId, cancellationToken);
        if (type is null || !type.IsActive)
        {
            return new DocumentCreateResult(DocumentWriteStatus.TypeUnavailable);
        }

        // Дружелюбный отказ до вставки; гонку добивает unique-индекс (catch ниже).
        if (!string.IsNullOrWhiteSpace(draft.RegNumber)
            && await db.Documents.AnyAsync(d => d.RegNumber == draft.RegNumber, cancellationToken))
        {
            return new DocumentCreateResult(DocumentWriteStatus.RegNumberTaken);
        }

        var isExecution = type.Group == DocumentGroup.Execution;

        // §3.2/§4.1: для «Исполнения» обязательны приоритет, инспектор и хотя бы одно назначение.
        if (isExecution && (draft.Priority is null || draft.InspectorUserId is null || assignments.Count == 0))
        {
            return new DocumentCreateResult(DocumentWriteStatus.ExecutionFieldsMissing);
        }

        var document = new Document
        {
            RegNumber = string.IsNullOrWhiteSpace(draft.RegNumber) ? null : draft.RegNumber.Trim(),
            RegDate = draft.RegDate,
            TypeId = draft.TypeId,
            DirectionFlag = draft.Direction,
            Source = draft.Source,
            ShortContent = draft.ShortContent.Trim(),
            FullText = draft.FullText,
            Notes = draft.Notes,
            // §1.4: у «Хранения» нет исполнения — приоритет и инспектор не применяются.
            Priority = isExecution ? draft.Priority : null,
            InspectorUserId = isExecution ? draft.InspectorUserId : null,
            AggregatedStatus = DocumentAggregatedStatus.NotApplicable,
            Classification = draft.Classification,
            DivisionId = draft.DivisionId,
            RegisteredByUserId = draft.RegisteredByUserId,
        };
        db.Documents.Add(document);

        // Созданные назначения удерживаются, чтобы после SaveChanges отдать их идентификаторы
        // вызывающему для уведомлений (см. CreatedDocumentNotice) — без повторного чтения документа.
        var createdAssignments = new List<DocumentAssignment>(assignments.Count);

        if (isExecution)
        {
            var now = DateTime.UtcNow;
            foreach (var assignmentDraft in assignments)
            {
                // §4.1: чекбокс «единый срок» проставляет один срок всем, иначе — индивидуальный.
                var assignment = new DocumentAssignment
                {
                    Document = document,
                    DivisionId = assignmentDraft.DivisionId,
                    AssigneeUserId = assignmentDraft.AssigneeUserId,
                    Deadline = useCommonDeadline ? commonDeadline : assignmentDraft.Deadline,
                    UseCommonDeadline = useCommonDeadline,
                    Status = AssignmentStatus.Registered,
                };
                db.DocumentAssignments.Add(assignment);
                createdAssignments.Add(assignment);

                // История: создание назначения = переход «ниоткуда» в «Зарегистрировано» (§4.8).
                db.AssignmentStatusHistories.Add(new AssignmentStatusHistory
                {
                    Assignment = assignment,
                    FromStatus = null,
                    ToStatus = AssignmentStatus.Registered,
                    ChangedByUserId = draft.RegisteredByUserId,
                    ChangedAt = now,
                });
            }

            // §4.3: N зарегистрированных назначений → агрегат «Зарегистрирован».
            document.AggregatedStatus = AggregatedStatusCalculator.Calculate(
                [.. assignments.Select(_ => AssignmentStatus.Registered)]);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Гонка по уникальному рег. номеру (unique-индекс) — дружелюбный отказ вместо 500.
            return new DocumentCreateResult(DocumentWriteStatus.RegNumberTaken);
        }

        // Данные для уведомлений отдаются ИЗ этой же операции: право исполнителя и инспектора получить
        // уведомление не должно зависеть от того, видит ли регистратор свой документ (см. CreatedDocumentNotice).
        var notice = new CreatedDocumentNotice(
            document.Id,
            DocumentTitle(document.RegNumber, document.ShortContent),
            document.InspectorUserId,
            [.. createdAssignments.Select(a => new CreatedAssignmentNotice(a.Id, a.AssigneeUserId, a.Deadline))]);

        return new DocumentCreateResult(DocumentWriteStatus.Ok, document.Id, notice);
    }

    /// <inheritdoc />
    public async Task<DocumentUpdateResult> UpdateAsync(
        DocumentEdit edit, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(edit);
        ArgumentNullException.ThrowIfNull(access);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Нельзя менять то, чего не видишь (WriteAccessRule): недоступный документ — тот же NotFound.
        // Берём ОТСЛЕЖИВАЕМУЮ сущность через тот же предикат видимости, а не db.Documents напрямую.
        var document = await VisibleDocuments(db, access)
            .FirstOrDefaultAsync(d => d.Id == edit.DocumentId, cancellationToken);
        if (document is null)
        {
            return new DocumentUpdateResult(DocumentWriteStatus.NotFound);
        }

        // --- Режимные правила правки (ТБ-020/021). Порядок проверок = порядок полей в форме. ---

        // Понижение грифа = рассекречивание, отдельная процедура (см. статус).
        if (edit.Classification < document.Classification)
        {
            return new DocumentUpdateResult(DocumentWriteStatus.ClassificationDowngradeNotAllowed);
        }

        // Поднять выше своего допуска нельзя: документ исчез бы у того, кто его правит.
        if (edit.Classification > access.MaxClassification)
        {
            return new DocumentUpdateResult(DocumentWriteStatus.ClassificationOutsideClearance);
        }

        // Подразделение меняем только на разрешённое — по той же причине.
        if (edit.DivisionId != document.DivisionId && !access.AllowedDivisions.Contains(edit.DivisionId))
        {
            return new DocumentUpdateResult(DocumentWriteStatus.DivisionOutsideClearance);
        }

        // --- Правила предметной области (§3.1/§3.2) ---

        var type = await db.DocumentTypes.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == edit.TypeId, cancellationToken);

        // Неактивный тип не предлагается (§3.1). Если тип документа НЕ МЕНЯЕТСЯ, его неактивность
        // правку не блокирует: иначе выведенный из обращения тип запирал бы старые документы навсегда.
        if (type is null || (!type.IsActive && edit.TypeId != document.TypeId))
        {
            return new DocumentUpdateResult(DocumentWriteStatus.TypeUnavailable);
        }

        var currentGroup = await db.DocumentTypes.AsNoTracking()
            .Where(t => t.Id == document.TypeId)
            .Select(t => (DocumentGroup?)t.Group)
            .FirstOrDefaultAsync(cancellationToken);

        if (currentGroup is { } group && group != type.Group)
        {
            return new DocumentUpdateResult(DocumentWriteStatus.TypeGroupChangeNotAllowed);
        }

        var isExecution = type.Group == DocumentGroup.Execution;

        // §3.2: у «Исполнения» приоритет и инспектор обязательны. Назначения здесь не проверяются —
        // они у документа уже есть (иначе он не был бы «Исполнением») и этой операцией не меняются.
        if (isExecution && (edit.Priority is null || edit.InspectorUserId is null))
        {
            return new DocumentUpdateResult(DocumentWriteStatus.ExecutionFieldsMissing);
        }

        var regNumber = string.IsNullOrWhiteSpace(edit.RegNumber) ? null : edit.RegNumber.Trim();

        // Уникальность рег. номера — ИСКЛЮЧАЯ сам документ, иначе сохранение без правки номера
        // отбивалось бы как «номер занят» им же самим.
        if (regNumber is not null && await db.Documents
                .AnyAsync(d => d.RegNumber == regNumber && d.Id != document.Id, cancellationToken))
        {
            return new DocumentUpdateResult(DocumentWriteStatus.RegNumberTaken);
        }

        var previousInspectorUserId = document.InspectorUserId;
        var changed = ChangedFields(document, edit, regNumber, isExecution);

        document.RegNumber = regNumber;
        document.RegDate = edit.RegDate;
        document.TypeId = edit.TypeId;
        document.DirectionFlag = edit.Direction;
        document.Source = edit.Source;
        document.ShortContent = edit.ShortContent.Trim();
        document.FullText = edit.FullText;
        document.Notes = edit.Notes;
        document.Priority = isExecution ? edit.Priority : null;
        document.InspectorUserId = isExecution ? edit.InspectorUserId : null;
        document.Classification = edit.Classification;
        document.DivisionId = edit.DivisionId;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Кто-то сохранил карточку, пока эта форма была открыта. Молча затирать чужую правку
            // нельзя — возвращаем конфликт, пользователь перечитает актуальные данные.
            return new DocumentUpdateResult(DocumentWriteStatus.Conflict);
        }
        catch (DbUpdateException) when (regNumber is not null)
        {
            // Гонку по рег. номеру добивает unique-индекс — проверка выше её не ловит.
            return new DocumentUpdateResult(DocumentWriteStatus.RegNumberTaken);
        }

        return new DocumentUpdateResult(
            DocumentWriteStatus.Ok,
            new UpdatedDocumentNotice(
                document.Id,
                DocumentTitle(document.RegNumber, document.ShortContent),
                document.Classification,
                document.DivisionId,
                changed,
                previousInspectorUserId,
                document.InspectorUserId));
    }

    /// <summary>
    /// Перечень изменённых реквизитов — ИМЕНАМИ, без значений.
    /// </summary>
    /// <remarks>
    /// В журнал аудита уходит именно этот список (ТБ-032): «изменено краткое содержание» достаточно
    /// для разбора, а копия самого содержания сделала бы журнал второй базой документов под грифом.
    /// </remarks>
    private static IReadOnlyList<string> ChangedFields(
        Document document, DocumentEdit edit, string? regNumber, bool isExecution)
    {
        var changed = new List<string>();

        void Track(bool differs, string name)
        {
            if (differs)
            {
                changed.Add(name);
            }
        }

        Track(document.RegNumber != regNumber, "рег. номер");
        Track(document.RegDate != edit.RegDate, "дата регистрации");
        Track(document.TypeId != edit.TypeId, "тип");
        Track(document.DirectionFlag != edit.Direction, "направленность");
        Track(document.Source != edit.Source, "источник");
        Track(document.ShortContent != edit.ShortContent.Trim(), "краткое содержание");
        Track(document.FullText != edit.FullText, "полный текст");
        Track(document.Notes != edit.Notes, "примечания");
        Track(document.Priority != (isExecution ? edit.Priority : null), "приоритет");
        Track(document.InspectorUserId != (isExecution ? edit.InspectorUserId : null), "инспектор");
        Track(document.Classification != edit.Classification, "гриф");
        Track(document.DivisionId != edit.DivisionId, "подразделение");

        return changed;
    }

    /// <inheritdoc />
    public async Task<DocumentPage> ListAsync(
        DocumentListFilter filter, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(access);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Решётка доступа В ЗАПРОСЕ, не пост-фильтром (инвариант 3, ТБ-020/021): гриф ≤ допуск И
        // подразделение ∈ разрешённых — тот же предикат, что у floor'а ядра (BaselineAccess) и
        // раздачи файлов (этап 4.3). Пустой список подразделений ⇒ пустая выдача (fail-closed).
        // Второй Where — СУЖАЮЩАЯ политика профиля (этап 6, ADR-0014): построчные правила по роли/
        // владению (§2.1 ТЗ СКИД). Docflow сам не знает, что такое «роль», — только зовёт нейтральный
        // порт; по умолчанию (без профиля с политикой) BuildFilter пропускает всё без изменений.
        var allowedDivisions = access.AllowedDivisions;
        var query = db.Documents.AsNoTracking()
            .Where(d => d.Classification <= access.MaxClassification
                && allowedDivisions.Contains(d.DivisionId))
            .Where(accessPolicy.BuildFilter<Document>(access));

        if (!string.IsNullOrWhiteSpace(filter.Text))
        {
            // §3.4: поиск по рег. номеру, краткому содержанию и ИСТОЧНИКУ (без учёта регистра).
            // Источник добавлен по образцу СКИД: документы часто ищут именно по отправителю,
            // а его название в краткое содержание попадает не всегда.
            var pattern = $"%{filter.Text.Trim()}%";
            query = query.Where(d =>
                (d.RegNumber != null && EF.Functions.ILike(d.RegNumber, pattern))
                || EF.Functions.ILike(d.ShortContent, pattern)
                || (d.Source != null && EF.Functions.ILike(d.Source, pattern)));
        }

        if (filter.Group is { } group)
        {
            query = query.Where(d => d.Type!.Group == group);
        }

        if (filter.TypeId is { } typeId)
        {
            query = query.Where(d => d.TypeId == typeId);
        }

        if (filter.AggregatedStatus is { } status)
        {
            query = query.Where(d => d.AggregatedStatus == status);
        }

        if (filter.Priority is { } priority)
        {
            query = query.Where(d => d.Priority == priority);
        }

        if (filter.InspectorUserId is { } inspectorUserId)
        {
            query = query.Where(d => d.InspectorUserId == inspectorUserId);
        }

        if (filter.DivisionId is { } divisionId)
        {
            query = query.Where(d => d.DivisionId == divisionId);
        }

        if (filter.RegDateFrom is { } from)
        {
            query = query.Where(d => d.RegDate >= from);
        }

        if (filter.RegDateTo is { } to)
        {
            query = query.Where(d => d.RegDate <= to);
        }

        // Общее число — ДО среза страницы: навигация должна знать, сколько всего подошло.
        var total = await query.CountAsync(cancellationToken);

        var page = Math.Max(1, filter.Page);
        var pageSize = Math.Clamp(filter.PageSize, 1, MaxPageSize);

        var rows = await query
            .OrderByDescending(d => d.RegDate).ThenByDescending(d => d.Id)
            .Select(d => new DocumentListItem(
                d.Id,
                d.RegNumber,
                d.RegDate,
                d.Type!.Name,
                d.Type!.Group,
                d.DirectionFlag,
                d.ShortContent,
                d.Priority,
                d.AggregatedStatus,
                d.Classification,
                d.DivisionId,
                d.Assignments.Count))
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new DocumentPage(rows, total);
    }

    /// <inheritdoc />
    public async Task<DocumentDetails?> GetAsync(
        int documentId, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Допуск — в самом запросе: документ вне допуска даёт null, неотличимый от «не найден»
        // (сам факт существования не подтверждается — то же решение, что 404 у раздачи файлов).
        // Второй Where — та же сужающая политика профиля, что и в ListAsync (см. комментарий там).
        var allowedDivisions = access.AllowedDivisions;
        return await db.Documents.AsNoTracking()
            .Where(d => d.Id == documentId
                && d.Classification <= access.MaxClassification
                && allowedDivisions.Contains(d.DivisionId))
            .Where(accessPolicy.BuildFilter<Document>(access))
            .Select(d => new DocumentDetails(
                d.Id,
                d.RegNumber,
                d.RegDate,
                d.TypeId,
                d.Type!.Name,
                d.Type!.Group,
                d.DirectionFlag,
                d.Source,
                d.ShortContent,
                d.FullText,
                d.Notes,
                d.Priority,
                d.InspectorUserId,
                d.AggregatedStatus,
                d.Classification,
                d.DivisionId,
                d.Assignments.OrderBy(a => a.Id).Select(a => new AssignmentDetails(
                    a.Id, a.DivisionId, a.AssigneeUserId, a.Status, a.Deadline, a.ControllerUserId)).ToList(),
                db.DocumentIndexLinks.Where(l => l.DocumentId == d.Id)
                    .Select(l => (DateTime?)l.IndexedAt).FirstOrDefault(),
                d.Files.OrderByDescending(f => f.IsLatest).ThenByDescending(f => f.Version)
                    .Select(f => new DocumentFileItem(
                        f.Id, f.FileName, f.Language, f.Version, f.IsLatest, f.FileSize, f.CreatedAt,
                        f.StoredFileName)).ToList(),
                db.DocumentAttachments.Where(a => a.DocumentId == d.Id).OrderBy(a => a.Id)
                    .Select(a => new AttachmentItem(a.Id, a.FileName, a.FileSize, a.CreatedAt, a.StoredFileName)).ToList()))
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<DocumentWriteStatus> ChangeAssignmentStatusAsync(
        int assignmentId,
        AssignmentStatus newStatus,
        string? comment,
        AccessContext access,
        IReadOnlyList<UploadedFile>? files = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Нельзя менять то, чего не видишь (WriteAccessRule): проверяется ДОКУМЕНТ-владелец назначения.
        if (!await IsAssignmentVisibleAsync(db, assignmentId, access, cancellationToken))
        {
            return DocumentWriteStatus.NotFound;
        }

        var changedByUserId = access.NumericSubjectId;

        var assignment = await db.DocumentAssignments
            .FirstOrDefaultAsync(a => a.Id == assignmentId, cancellationToken);
        if (assignment is null)
        {
            return DocumentWriteStatus.NotFound;
        }

        // §4.2: «Просрочено» ставит ТОЛЬКО система по сроку (фоновая проверка — этап 4), не человек.
        if (newStatus == AssignmentStatus.Overdue)
        {
            return DocumentWriteStatus.OverdueIsAutomatic;
        }

        if (!StatusTransitionMatrix.IsTransitionAllowed(assignment.Status, newStatus))
        {
            return DocumentWriteStatus.InvalidTransition;
        }

        var oldStatus = assignment.Status;
        assignment.Status = newStatus;

        // §4.2 «Контроль»: на входе фиксируем контролёра, на выходе сбрасываем (как в СКИД).
        if (newStatus == AssignmentStatus.InControl)
        {
            assignment.ControllerUserId = changedByUserId;
        }
        else if (oldStatus == AssignmentStatus.InControl)
        {
            assignment.ControllerUserId = null;
        }

        var historyEntry = new AssignmentStatusHistory
        {
            AssignmentId = assignment.Id,
            FromStatus = oldStatus,
            ToStatus = newStatus,
            ChangedByUserId = changedByUserId,
            ChangedAt = DateTime.UtcNow,
            Comment = comment,
        };
        db.AssignmentStatusHistories.Add(historyEntry);

        // §4.2: на каждом переходе можно приложить файлы — сохраняем ДО SaveChanges,
        // при сбое БД компенсирующе удаляем (как в СКИД).
        var savedFiles = new List<string>();
        var subPath = assignment.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            foreach (var file in files ?? [])
            {
                using var content = new MemoryStream(file.Content);
                var storedFileName = await fileStorage.SaveAsync(
                    content, Path.GetExtension(file.FileName), FileCategories.StatusHistory, subPath,
                    cancellationToken);
                savedFiles.Add(storedFileName);

                db.StatusHistoryFiles.Add(new StatusHistoryFile
                {
                    StatusHistory = historyEntry,
                    FileName = file.FileName,
                    StoredFileName = storedFileName,
                    ContentType = file.ContentType,
                    FileSize = file.Content.LongLength,
                    UploadedByUserId = changedByUserId ?? 0,
                });
            }

            // Атомарно: статус + история + файлы (один SaveChanges); конкуренцию ловит xmin назначения.
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            await CleanupFilesAsync(savedFiles, FileCategories.StatusHistory, subPath);
            return DocumentWriteStatus.Conflict;
        }
        catch
        {
            await CleanupFilesAsync(savedFiles, FileCategories.StatusHistory, subPath);
            throw;
        }

        await RecalculateAggregateAsync(db, assignment.DocumentId, cancellationToken);
        return DocumentWriteStatus.Ok;
    }

    private async Task CleanupFilesAsync(List<string> storedFileNames, string category, string subPath)
    {
        foreach (var storedFileName in storedFileNames)
        {
            await fileStorage.DeleteAsync(storedFileName, category, subPath, CancellationToken.None);
        }
    }

    /// <inheritdoc />
    public async Task<DocumentWriteStatus> ExtendDeadlineAsync(
        int assignmentId,
        DateOnly newDeadline,
        string reason,
        AccessContext access,
        IReadOnlyList<UploadedFile>? files = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentNullException.ThrowIfNull(access);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Нельзя менять то, чего не видишь (WriteAccessRule).
        if (!await IsAssignmentVisibleAsync(db, assignmentId, access, cancellationToken))
        {
            return DocumentWriteStatus.NotFound;
        }

        // §4.6: инициатор фиксируется обязательно — сценарий уже отверг запрос без числового субъекта.
        var initiatedByUserId = access.NumericSubjectId ?? 0;

        var assignment = await db.DocumentAssignments
            .FirstOrDefaultAsync(a => a.Id == assignmentId, cancellationToken);
        if (assignment is null)
        {
            return DocumentWriteStatus.NotFound;
        }

        // Продление снятого с контроля назначения смысла не имеет — финальный статус (§4.2).
        if (assignment.Status == AssignmentStatus.Closed)
        {
            return DocumentWriteStatus.InvalidTransition;
        }

        if (assignment.Deadline is not { } oldDeadline)
        {
            return DocumentWriteStatus.NoDeadline;
        }

        var now = DateTime.UtcNow;

        // §4.6: продление фиксируется с обязательным основанием; количество не ограничено.
        var extension = new DeadlineExtension
        {
            AssignmentId = assignment.Id,
            OldDeadline = oldDeadline,
            NewDeadline = newDeadline,
            Reason = reason.Trim(),
            InitiatedByUserId = initiatedByUserId,
        };
        db.DeadlineExtensions.Add(extension);

        // §4.6: к продлению можно приложить файлы-обоснования (компенсация — как у переходов).
        var savedFiles = new List<string>();
        var filesSubPath = assignment.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (var file in files ?? [])
        {
            using var content = new MemoryStream(file.Content);
            var storedFileName = await fileStorage.SaveAsync(
                content, Path.GetExtension(file.FileName), FileCategories.DeadlineExtensions, filesSubPath,
                cancellationToken);
            savedFiles.Add(storedFileName);

            db.DeadlineExtensionFiles.Add(new DeadlineExtensionFile
            {
                Extension = extension,
                FileName = file.FileName,
                StoredFileName = storedFileName,
                ContentType = file.ContentType,
                FileSize = file.Content.LongLength,
                UploadedByUserId = initiatedByUserId,
            });
        }

        assignment.Deadline = newDeadline;

        // §4.6: после продления назначение автоматически возвращается «В работу» (переход — в историю).
        if (assignment.Status != AssignmentStatus.InProgress)
        {
            db.AssignmentStatusHistories.Add(new AssignmentStatusHistory
            {
                AssignmentId = assignment.Id,
                FromStatus = assignment.Status,
                ToStatus = AssignmentStatus.InProgress,
                ChangedByUserId = initiatedByUserId,
                ChangedAt = now,
            });
            assignment.Status = AssignmentStatus.InProgress;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            await CleanupFilesAsync(savedFiles, FileCategories.DeadlineExtensions, filesSubPath);
            return DocumentWriteStatus.Conflict;
        }
        catch
        {
            await CleanupFilesAsync(savedFiles, FileCategories.DeadlineExtensions, filesSubPath);
            throw;
        }

        await RecalculateAggregateAsync(db, assignment.DocumentId, cancellationToken);
        return DocumentWriteStatus.Ok;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<OverdueMark>> MarkOverdueAsync(
        DateOnly today, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Кандидаты read-only списком id; перевод — поштучно (перенос подхода СКИД SAD §9.3):
        // конкурентное изменение одного назначения не должно отравить остальные.
        var candidateIds = await db.DocumentAssignments.AsNoTracking()
            .Where(a => a.Deadline != null && a.Deadline < today
                && a.Status != AssignmentStatus.Done
                && a.Status != AssignmentStatus.Closed
                && a.Status != AssignmentStatus.Overdue)
            .Select(a => a.Id)
            .ToListAsync(cancellationToken);

        var marked = new List<(int AssignmentId, int DocumentId)>();
        var affectedDocuments = new HashSet<int>();

        foreach (var id in candidateIds)
        {
            await using var itemDb = await contextFactory.CreateDbContextAsync(cancellationToken);
            var assignment = await itemDb.DocumentAssignments
                .FirstOrDefaultAsync(a => a.Id == id, cancellationToken);

            // Перепроверка под трекингом: пока ждали, статус мог смениться (исполнили/сняли/продлили).
            if (assignment is null
                || assignment.Status is AssignmentStatus.Done or AssignmentStatus.Closed or AssignmentStatus.Overdue
                || assignment.Deadline is null || assignment.Deadline >= today)
            {
                continue;
            }

            var oldStatus = assignment.Status;
            assignment.Status = AssignmentStatus.Overdue;
            itemDb.AssignmentStatusHistories.Add(new AssignmentStatusHistory
            {
                AssignmentId = assignment.Id,
                FromStatus = oldStatus,
                ToStatus = AssignmentStatus.Overdue,
                ChangedByUserId = null, // системное действие (§4.2: «Просрочено» ставит система)
                ChangedAt = DateTime.UtcNow,
                Comment = $"Просрочено автоматически: срок {assignment.Deadline:dd.MM.yyyy} истёк",
            });

            try
            {
                await itemDb.SaveChangesAsync(cancellationToken);
                marked.Add((assignment.Id, assignment.DocumentId));
                affectedDocuments.Add(assignment.DocumentId);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Кто-то менял назначение параллельно — пропускаем, следующий тик перепроверит.
            }
        }

        // Агрегаты — по разу на затронутый документ.
        foreach (var documentId in affectedDocuments)
        {
            await RecalculateAggregateAsync(db, documentId, cancellationToken);
        }

        if (marked.Count == 0)
        {
            return [];
        }

        // Гриф/подразделение владеющих документов — иначе аудит перевода классифицировал бы запись
        // грифом 0 независимо от реального грифа объекта (ТБ-032, см. OverdueMark). Здесь же —
        // данные для уведомления о просрочке (разд. 5): заголовок и инспектор документа.
        var documentInfo = await db.Documents.AsNoTracking()
            .Where(d => affectedDocuments.Contains(d.Id))
            .Select(d => new
            {
                d.Id, d.Classification, d.DivisionId, d.RegNumber, d.ShortContent, d.InspectorUserId,
            })
            .ToDictionaryAsync(d => d.Id, cancellationToken);

        var markedIds = marked.Select(m => m.AssignmentId).ToList();
        var assignees = await db.DocumentAssignments.AsNoTracking()
            .Where(a => markedIds.Contains(a.Id))
            .Select(a => new { a.Id, a.AssigneeUserId, a.Deadline })
            .ToDictionaryAsync(a => a.Id, cancellationToken);

        return marked
            .Select(m =>
            {
                var document = documentInfo[m.DocumentId];
                var assignment = assignees[m.AssignmentId];
                return new OverdueMark(
                    m.AssignmentId,
                    document.Classification,
                    document.DivisionId,
                    m.DocumentId,
                    DocumentTitle(document.RegNumber, document.ShortContent),
                    assignment.Deadline,
                    assignment.AssigneeUserId,
                    document.InspectorUserId);
            })
            .ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DeadlineNotice>> FindDeadlineNoticesAsync(
        DateOnly from, DateOnly until, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await db.DocumentAssignments.AsNoTracking()
            .Where(a => a.Deadline != null && a.Deadline >= from && a.Deadline <= until
                && a.Status != AssignmentStatus.Done
                && a.Status != AssignmentStatus.Closed
                && a.Status != AssignmentStatus.Overdue)
            .Join(db.Documents, a => a.DocumentId, d => d.Id, (a, d) => new DeadlineNotice(
                a.Id,
                d.Id,
                d.RegNumber != null ? d.RegNumber : "б/н «" + d.ShortContent + "»",
                a.Deadline!.Value,
                a.AssigneeUserId,
                d.InspectorUserId))
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<AssignmentParticipants?> GetAssignmentParticipantsAsync(
        int assignmentId, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Соединение ИМЕННО с VisibleDocuments, а не с db.Documents: недоступный документ не должен
        // раскрывать даже состав участников своего назначения (ТБ-020/021, WriteAccessRule).
        return await db.DocumentAssignments.AsNoTracking()
            .Where(a => a.Id == assignmentId)
            .Join(VisibleDocuments(db, access), a => a.DocumentId, d => d.Id, (a, d) => new AssignmentParticipants(
                a.Id,
                d.Id,
                d.RegNumber != null ? d.RegNumber : "б/н «" + d.ShortContent + "»",
                a.Deadline,
                a.AssigneeUserId,
                d.InspectorUserId,
                a.ControllerUserId))
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<AssignmentAddResult> AddAssignmentAsync(
        int documentId, AssignmentDraft draft, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(access);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Нельзя менять то, чего не видишь (WriteAccessRule).
        if (!await IsDocumentVisibleAsync(db, documentId, access, cancellationToken))
        {
            return new AssignmentAddResult(DocumentWriteStatus.NotFound);
        }

        var document = await db.Documents
            .Where(d => d.Id == documentId)
            .Join(db.DocumentTypes, d => d.TypeId, t => t.Id, (d, t) => new { Document = d, t.Group })
            .FirstOrDefaultAsync(cancellationToken);
        if (document is null)
        {
            return new AssignmentAddResult(DocumentWriteStatus.NotFound);
        }

        // §4.1: у «Хранения» назначений нет по построению — иначе агрегат уехал бы с «неприменимо».
        if (document.Group != DocumentGroup.Execution)
        {
            return new AssignmentAddResult(DocumentWriteStatus.NotExecutionGroup);
        }

        // Запрет дубля подразделения. В СКИД он жил ТОЛЬКО в валидаторе формы — уникального индекса не
        // было, и два одновременных запроса создавали два назначения на одно подразделение. Здесь
        // проверка в БД, а гонку добивает уникальный индекс (см. миграцию AssignmentReassignments).
        if (await db.DocumentAssignments
                .AnyAsync(a => a.DocumentId == documentId && a.DivisionId == draft.DivisionId, cancellationToken))
        {
            return new AssignmentAddResult(DocumentWriteStatus.AssignmentDivisionTaken);
        }

        if (draft.AssigneeUserId is { } assignee
            && !await users.CanSeeDivisionAsync(assignee, draft.DivisionId, cancellationToken))
        {
            return new AssignmentAddResult(DocumentWriteStatus.AssigneeOutsideDivision);
        }

        var assignment = new DocumentAssignment
        {
            DocumentId = documentId,
            DivisionId = draft.DivisionId,
            AssigneeUserId = draft.AssigneeUserId,
            // Срок ВСЕГДА индивидуальный: «единый срок» §4.1 действует только на назначения,
            // созданные при регистрации, и на добавленное позже не распространяется (решение СКИД).
            Deadline = draft.Deadline,
            UseCommonDeadline = false,
            Status = AssignmentStatus.Registered,
        };
        db.DocumentAssignments.Add(assignment);

        db.AssignmentStatusHistories.Add(new AssignmentStatusHistory
        {
            Assignment = assignment,
            FromStatus = null,
            ToStatus = AssignmentStatus.Registered,
            ChangedByUserId = access.NumericSubjectId,
            ChangedAt = DateTime.UtcNow,
        });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Гонка по уникальному индексу (документ, подразделение) — дружелюбный отказ вместо 500.
            return new AssignmentAddResult(DocumentWriteStatus.AssignmentDivisionTaken);
        }

        await RecalculateAggregateAsync(db, documentId, cancellationToken);

        var notice = new CreatedDocumentNotice(
            documentId,
            DocumentTitle(document.Document.RegNumber, document.Document.ShortContent),
            document.Document.InspectorUserId,
            [new CreatedAssignmentNotice(assignment.Id, assignment.AssigneeUserId, assignment.Deadline)]);

        return new AssignmentAddResult(DocumentWriteStatus.Ok, assignment.Id, notice);
    }

    /// <inheritdoc />
    public async Task<AssignmentReassignResult> ReassignAssigneeAsync(
        int assignmentId, int newAssigneeUserId, string? reason, AccessContext access,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        if (!await IsAssignmentVisibleAsync(db, assignmentId, access, cancellationToken))
        {
            return new AssignmentReassignResult(DocumentWriteStatus.NotFound);
        }

        var assignment = await db.DocumentAssignments
            .FirstOrDefaultAsync(a => a.Id == assignmentId, cancellationToken);
        if (assignment is null)
        {
            return new AssignmentReassignResult(DocumentWriteStatus.NotFound);
        }

        // Ужесточение против СКИД (там переназначали из ЛЮБОГО статуса): исполненное и снятое с
        // контроля назначение переписывать нельзя — это переписывание уже состоявшегося факта.
        // Граница та же, что у продления срока §4.6.
        if (assignment.Status is AssignmentStatus.Done or AssignmentStatus.Closed)
        {
            return new AssignmentReassignResult(DocumentWriteStatus.InvalidTransition);
        }

        // Тот же исполнитель — менять нечего. В СКИД такой вызов проходил, писал в аудит «было = стало»
        // и слал человеку «вы назначены исполнителем»; здесь операция идемпотентна и молчалива.
        if (assignment.AssigneeUserId == newAssigneeUserId)
        {
            return new AssignmentReassignResult(DocumentWriteStatus.Ok);
        }

        if (!await users.CanSeeDivisionAsync(newAssigneeUserId, assignment.DivisionId, cancellationToken))
        {
            return new AssignmentReassignResult(DocumentWriteStatus.AssigneeOutsideDivision);
        }

        var previousAssigneeUserId = assignment.AssigneeUserId;
        assignment.AssigneeUserId = newAssigneeUserId;

        // Статус, срок и контролёр НЕ трогаем (решение СКИД): новый исполнитель наследует срок
        // прежнего, счёт времени заново не начинается.
        db.AssignmentReassignments.Add(new AssignmentReassignment
        {
            AssignmentId = assignment.Id,
            FromUserId = previousAssigneeUserId,
            ToUserId = newAssigneeUserId,
            ChangedByUserId = access.NumericSubjectId,
            ChangedAt = DateTime.UtcNow,
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
        });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new AssignmentReassignResult(DocumentWriteStatus.Conflict);
        }

        var document = await db.Documents.AsNoTracking()
            .Where(d => d.Id == assignment.DocumentId)
            .Select(d => new { d.RegNumber, d.ShortContent, d.InspectorUserId })
            .FirstAsync(cancellationToken);

        return new AssignmentReassignResult(
            DocumentWriteStatus.Ok,
            new ReassignedNotice(
                assignment.Id,
                assignment.DocumentId,
                DocumentTitle(document.RegNumber, document.ShortContent),
                previousAssigneeUserId,
                newAssigneeUserId,
                assignment.Deadline,
                document.InspectorUserId));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AssignmentTimelineEvent>?> GetAssignmentTimelineAsync(
        int assignmentId, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Чтение — тот же фильтр допуска, что у карточки: недоступное неотличимо от несуществующего.
        if (!await IsAssignmentVisibleAsync(db, assignmentId, access, cancellationToken))
        {
            return null;
        }

        var history = await db.AssignmentStatusHistories.AsNoTracking()
            .Where(h => h.AssignmentId == assignmentId)
            .Select(h => new
            {
                h.Id, h.FromStatus, h.ToStatus, h.ChangedByUserId, h.ChangedAt, h.Comment,
            })
            .ToListAsync(cancellationToken);

        var historyIds = history.Select(h => h.Id).ToList();
        var historyFiles = await db.StatusHistoryFiles.AsNoTracking()
            .Where(f => historyIds.Contains(f.StatusHistoryId))
            .Select(f => new
            {
                f.StatusHistoryId,
                File = new AssignmentTimelineFile(
                    f.Id, f.FileName, f.ContentType, f.FileSize, f.StoredFileName, FileCategories.StatusHistory),
            })
            .ToListAsync(cancellationToken);

        var extensions = await db.DeadlineExtensions.AsNoTracking()
            .Where(e => e.AssignmentId == assignmentId)
            .Select(e => new
            {
                e.Id, e.OldDeadline, e.NewDeadline, e.Reason, e.InitiatedByUserId, e.CreatedAt,
            })
            .ToListAsync(cancellationToken);

        var reassignments = await db.AssignmentReassignments.AsNoTracking()
            .Where(r => r.AssignmentId == assignmentId)
            .Select(r => new { r.FromUserId, r.ToUserId, r.ChangedByUserId, r.ChangedAt, r.Reason })
            .ToListAsync(cancellationToken);

        var extensionIds = extensions.Select(e => e.Id).ToList();
        var extensionFiles = await db.DeadlineExtensionFiles.AsNoTracking()
            .Where(f => extensionIds.Contains(f.ExtensionId))
            .Select(f => new
            {
                f.ExtensionId,
                File = new AssignmentTimelineFile(
                    f.Id, f.FileName, f.ContentType, f.FileSize, f.StoredFileName,
                    FileCategories.DeadlineExtensions),
            })
            .ToListAsync(cancellationToken);

        var filesByHistory = historyFiles.GroupBy(f => f.StatusHistoryId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<AssignmentTimelineFile>)[.. g.Select(x => x.File)]);
        var filesByExtension = extensionFiles.GroupBy(f => f.ExtensionId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<AssignmentTimelineFile>)[.. g.Select(x => x.File)]);

        var events = history
            .Select(h => new AssignmentTimelineEvent(
                // Создание назначения отличается от смены статуса ровно отсутствием FromStatus
                // (переход «ниоткуда» — так его и пишет CreateAsync).
                h.FromStatus is null ? AssignmentEventKind.Created : AssignmentEventKind.StatusChanged,
                h.ChangedAt,
                h.ChangedByUserId,
                h.FromStatus,
                h.ToStatus,
                OldDeadline: null,
                NewDeadline: null,
                h.Comment,
                filesByHistory.TryGetValue(h.Id, out var hf) ? hf : []))
            .Concat(extensions.Select(e => new AssignmentTimelineEvent(
                AssignmentEventKind.DeadlineExtended,
                e.CreatedAt,
                e.InitiatedByUserId,
                FromStatus: null,
                ToStatus: null,
                e.OldDeadline,
                e.NewDeadline,
                e.Reason,
                filesByExtension.TryGetValue(e.Id, out var ef) ? ef : [])))
            .Concat(reassignments.Select(r => new AssignmentTimelineEvent(
                AssignmentEventKind.Reassigned,
                r.ChangedAt,
                r.ChangedByUserId,
                FromStatus: null,
                ToStatus: null,
                OldDeadline: null,
                NewDeadline: null,
                r.Reason,
                [],
                r.FromUserId,
                r.ToUserId)))
            .OrderBy(e => e.OccurredAt)
            .ToList();

        return events;
    }

    /// <summary>Обозначение документа для уведомлений — общее правило домена (см. NotificationTemplates).</summary>
    private static string DocumentTitle(string? regNumber, string shortContent) =>
        NotificationTemplates.DocumentTitle(regNumber, shortContent);

    /// <summary>
    /// Пересчёт агрегированного статуса документа (§4.3) по чистой функции. Пишется через
    /// <c>ExecuteUpdateAsync</c> НАМЕРЕННО мимо xmin (перенос решения СКИД, их SAD §15.3):
    /// параллельные смены статусов РАЗНЫХ назначений одного документа не должны конфликтовать
    /// между собой на строке документа.
    /// </summary>
    private static async Task RecalculateAggregateAsync(
        DocFlowDbContext db, int documentId, CancellationToken cancellationToken)
    {
        var statuses = await db.DocumentAssignments.AsNoTracking()
            .Where(a => a.DocumentId == documentId)
            .Select(a => a.Status)
            .ToListAsync(cancellationToken);

        var aggregated = AggregatedStatusCalculator.Calculate(statuses);
        var now = DateTime.UtcNow;

        await db.Documents
            .Where(d => d.Id == documentId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(d => d.AggregatedStatus, aggregated)
                .SetProperty(d => d.UpdatedAt, now), cancellationToken);
    }
}
