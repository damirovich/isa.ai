using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Domain.Entities;
using ISC.AI.Modules.DocFlow.Domain.Enums;
using ISC.AI.Modules.DocFlow.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Modules.DocFlow.Data;

/// <summary>
/// Назначения (§4.1–4.7): смена статуса по матрице §4.5, продление §4.6, переназначение §4.7,
/// авто-«Просрочено» §4.2, лента событий §4.8 и пересчёт агрегированного статуса §4.3.
/// Partial-файл — см. пояснение в DocumentStore.Files.cs.
/// </summary>
public sealed partial class DocumentStore
{
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
