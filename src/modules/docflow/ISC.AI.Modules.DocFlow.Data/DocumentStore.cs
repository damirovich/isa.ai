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
public sealed partial class DocumentStore(
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
    /// Виден ли документ субъекту. Сам предикат — в <see cref="AccessFilterExtensions.VisibleTo"/>
    /// (единственное место, общее с отчётами, дашбордом и уведомлениями); и чтение
    /// (<c>ListAsync</c>/<c>GetAsync</c>), и все проверки записи идут через него, чтобы правила не
    /// разъехались: однажды они и разъехались — чтение сузили, запись забыли (6.4.2).
    /// </summary>
    private IQueryable<Document> VisibleDocuments(DocFlowDbContext db, AccessContext access) =>
        db.Documents.VisibleTo(access, accessPolicy);

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

    /// <summary>Обозначение документа для уведомлений — общее правило домена (см. NotificationTemplates).</summary>
    private static string DocumentTitle(string? regNumber, string shortContent) =>
        NotificationTemplates.DocumentTitle(regNumber, shortContent);
}
