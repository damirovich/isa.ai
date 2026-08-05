using ISC.AI.Modules.DocFlow.Domain.Entities;
using ISC.AI.Modules.DocFlow.Domain.Enums;
using ISC.AI.Modules.DocFlow.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Modules.DocFlow.Data;

/// <summary>
/// Хранилище документов и назначений поверх <see cref="DocFlowDbContext"/> (ТЗ СКИД §3–4).
/// Контекст — на операцию, через фабрику (ТС-008, Blazor Server).
/// </summary>
public sealed class DocumentStore(IDbContextFactory<DocFlowDbContext> contextFactory) : IDocumentStore
{
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
                    a.Id, a.DivisionId, a.AssigneeUserId, a.Status, a.Deadline, a.ControllerUserId)).ToList()))
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<DocumentWriteStatus> ChangeAssignmentStatusAsync(
        int assignmentId,
        AssignmentStatus newStatus,
        string? comment,
        int? changedByUserId,
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

        db.AssignmentStatusHistories.Add(new AssignmentStatusHistory
        {
            AssignmentId = assignment.Id,
            FromStatus = oldStatus,
            ToStatus = newStatus,
            ChangedByUserId = changedByUserId,
            ChangedAt = DateTime.UtcNow,
            Comment = comment,
        });

        try
        {
            // Атомарно: статус + история (один SaveChanges); конкуренцию ловит xmin назначения.
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return DocumentWriteStatus.Conflict;
        }

        await RecalculateAggregateAsync(db, assignment.DocumentId, cancellationToken);
        return DocumentWriteStatus.Ok;
    }

    /// <inheritdoc />
    public async Task<DocumentWriteStatus> ExtendDeadlineAsync(
        int assignmentId,
        DateOnly newDeadline,
        string reason,
        int initiatedByUserId,
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
        db.DeadlineExtensions.Add(new DeadlineExtension
        {
            AssignmentId = assignment.Id,
            OldDeadline = oldDeadline,
            NewDeadline = newDeadline,
            Reason = reason.Trim(),
            InitiatedByUserId = initiatedByUserId,
        });

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
            return DocumentWriteStatus.Conflict;
        }

        await RecalculateAggregateAsync(db, assignment.DocumentId, cancellationToken);
        return DocumentWriteStatus.Ok;
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
