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
    IDocFlowFileStorage fileStorage) : IDocumentStore
{
    /// <inheritdoc />
    public async Task<DocumentWriteStatus> AddDocumentFileAsync(
        int documentId, UploadedFile file, DocumentLanguage language, int? uploadedByUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await db.Documents.AnyAsync(d => d.Id == documentId, cancellationToken))
        {
            return DocumentWriteStatus.NotFound;
        }

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
        int documentId, UploadedFile file, int? uploadedByUserId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(file);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await db.Documents.AnyAsync(d => d.Id == documentId, cancellationToken))
        {
            return DocumentWriteStatus.NotFound;
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
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(assignments);

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

        return new DocumentCreateResult(DocumentWriteStatus.Ok, document.Id);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DocumentListItem>> ListAsync(
        DocumentListFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var query = db.Documents.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(filter.Text))
        {
            // §3.4: поиск по рег. номеру и ключевым словам краткого содержания (без учёта регистра).
            var pattern = $"%{filter.Text.Trim()}%";
            query = query.Where(d =>
                (d.RegNumber != null && EF.Functions.ILike(d.RegNumber, pattern))
                || EF.Functions.ILike(d.ShortContent, pattern));
        }

        if (filter.TypeId is { } typeId)
        {
            query = query.Where(d => d.TypeId == typeId);
        }

        if (filter.AggregatedStatus is { } status)
        {
            query = query.Where(d => d.AggregatedStatus == status);
        }

        if (filter.RegDateFrom is { } from)
        {
            query = query.Where(d => d.RegDate >= from);
        }

        if (filter.RegDateTo is { } to)
        {
            query = query.Where(d => d.RegDate <= to);
        }

        return await query
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
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<DocumentDetails?> GetAsync(int documentId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await db.Documents.AsNoTracking()
            .Where(d => d.Id == documentId)
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
                        f.Id, f.FileName, f.Language, f.Version, f.IsLatest, f.FileSize, f.CreatedAt)).ToList(),
                db.DocumentAttachments.Where(a => a.DocumentId == d.Id).OrderBy(a => a.Id)
                    .Select(a => new AttachmentItem(a.Id, a.FileName, a.FileSize, a.CreatedAt)).ToList()))
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<DocumentWriteStatus> ChangeAssignmentStatusAsync(
        int assignmentId,
        AssignmentStatus newStatus,
        string? comment,
        int? changedByUserId,
        IReadOnlyList<UploadedFile>? files = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

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
        int initiatedByUserId,
        IReadOnlyList<UploadedFile>? files = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

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
    public async Task<IReadOnlyList<int>> MarkOverdueAsync(
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

        var marked = new List<int>();
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
                marked.Add(assignment.Id);
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

        return marked;
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
