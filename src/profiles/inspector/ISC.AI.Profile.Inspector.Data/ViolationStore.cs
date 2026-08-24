using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Profile.Inspector.Domain.Entities;
using ISC.AI.Profile.Inspector.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Profile.Inspector.Data;

/// <summary>
/// Учёт нарушений (<see cref="IViolationStore"/>, Э5-01) в схеме <c>inspector</c>.
/// Признак повторности НЕ хранится — производный (ещё одно нарушение того же вида в том же
/// подразделении), считается при выборке страницы: гранула маленькая (25 строк), а хранение
/// признака разъезжалось бы с фактом при правках.
/// </summary>
public sealed class ViolationStore(IDbContextFactory<InspectorDbContext> contextFactory) : IViolationStore
{
    /// <summary>Предел размера страницы — тот же, что у остальных реестров.</summary>
    public const int MaxPageSize = 200;

    /// <inheritdoc />
    public async Task<ViolationPage> ListAsync(
        ViolationListFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var query = db.Violations.AsNoTracking();

        if (filter.DivisionId is { } divisionId)
        {
            query = query.Where(v => v.DivisionId == divisionId);
        }

        if (filter.CategoryId is { } categoryId)
        {
            // Выбор сферы (верхний уровень) означает все её виды; выбор вида — только его.
            query = query.Where(v => v.CategoryId == categoryId || v.Category!.ParentId == categoryId);
        }

        if (filter.Severity is { } severity)
        {
            query = query.Where(v => v.Severity == severity);
        }

        if (filter.RemediationStatus is { } remediation)
        {
            query = query.Where(v => v.RemediationStatus == remediation);
        }

        if (filter.DetectedFrom is { } from)
        {
            query = query.Where(v => v.DetectedAt >= from);
        }

        if (filter.DetectedTo is { } to)
        {
            query = query.Where(v => v.DetectedAt <= to);
        }

        // Общее число — ДО среза страницы.
        var total = await query.CountAsync(cancellationToken);

        var page = Math.Max(1, filter.Page);
        var pageSize = Math.Clamp(filter.PageSize, 1, MaxPageSize);

        // Проекция в анонимный тип (record в EF-проекции переводится не всегда — общий обход проекта).
        var rows = await query
            .OrderByDescending(v => v.DetectedAt).ThenByDescending(v => v.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(v => new
            {
                v.Id,
                v.DivisionId,
                DivisionName = v.Division!.Name,
                v.CategoryId,
                CategoryName = v.Category!.Name,
                CategoryParentName = v.Category!.Parent != null ? v.Category.Parent.Name : null,
                v.Severity,
                v.DetectedAt,
                v.RemediationStatus,
                v.SourceDocRef,
                // Повторность — производная (Приложение §4): есть ли ДРУГОЕ нарушение того же вида
                // в том же подразделении (в любое время).
                IsRecurring = db.Violations.Any(o => o.Id != v.Id
                    && o.DivisionId == v.DivisionId && o.CategoryId == v.CategoryId),
            })
            .ToListAsync(cancellationToken);

        var items = rows
            .Select(r => new ViolationListItem(
                r.Id, r.DivisionId, r.DivisionName, r.CategoryId, r.CategoryName, r.CategoryParentName,
                r.Severity, r.DetectedAt, r.RemediationStatus, r.SourceDocRef, r.IsRecurring))
            .ToList();

        return new ViolationPage(items, total);
    }

    /// <inheritdoc />
    public async Task<ViolationDetails?> GetAsync(int violationId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.Violations.AsNoTracking()
            .Where(v => v.Id == violationId)
            .Select(v => new
            {
                v.Id, v.DivisionId, v.CategoryId, v.Severity, v.DetectedAt, v.RemediationStatus,
                v.SourceDocRef, v.SourceAssignmentRef, v.ReferenceDocRef, v.Cause, v.Recommendation,
            })
            .FirstOrDefaultAsync(cancellationToken);
        return rows is null
            ? null
            : new ViolationDetails(
                rows.Id, rows.DivisionId, rows.CategoryId, rows.Severity, rows.DetectedAt,
                rows.RemediationStatus, rows.SourceDocRef, rows.SourceAssignmentRef,
                rows.ReferenceDocRef, rows.Cause, rows.Recommendation);
    }

    /// <inheritdoc />
    public async Task<(ViolationWriteResult Result, int ViolationId)> CreateAsync(
        ViolationDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var check = await CheckReferencesAsync(db, draft, cancellationToken);
        if (check != ViolationWriteResult.Ok)
        {
            return (check, 0);
        }

        var violation = new Violation
        {
            DivisionId = draft.DivisionId,
            CategoryId = draft.CategoryId,
            Severity = draft.Severity,
            DetectedAt = draft.DetectedAt,
            RemediationStatus = draft.RemediationStatus,
            SourceDocRef = Normalize(draft.SourceDocRef),
            SourceAssignmentRef = Normalize(draft.SourceAssignmentRef),
            ReferenceDocRef = Normalize(draft.ReferenceDocRef),
            Cause = Normalize(draft.Cause),
            Recommendation = Normalize(draft.Recommendation),
        };
        db.Violations.Add(violation);
        await db.SaveChangesAsync(cancellationToken);

        return (ViolationWriteResult.Ok, violation.Id);
    }

    /// <inheritdoc />
    public async Task<ViolationWriteResult> UpdateAsync(
        int violationId, ViolationDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var violation = await db.Violations.FirstOrDefaultAsync(v => v.Id == violationId, cancellationToken);
        if (violation is null)
        {
            return ViolationWriteResult.NotFound;
        }

        var check = await CheckReferencesAsync(db, draft, cancellationToken);
        if (check != ViolationWriteResult.Ok)
        {
            return check;
        }

        violation.DivisionId = draft.DivisionId;
        violation.CategoryId = draft.CategoryId;
        violation.Severity = draft.Severity;
        violation.DetectedAt = draft.DetectedAt;
        violation.RemediationStatus = draft.RemediationStatus;
        violation.SourceDocRef = Normalize(draft.SourceDocRef);
        violation.SourceAssignmentRef = Normalize(draft.SourceAssignmentRef);
        violation.ReferenceDocRef = Normalize(draft.ReferenceDocRef);
        violation.Cause = Normalize(draft.Cause);
        violation.Recommendation = Normalize(draft.Recommendation);
        await db.SaveChangesAsync(cancellationToken);

        return ViolationWriteResult.Ok;
    }

    // Подразделение и категория существуют. Категория — ЛЮБОГО уровня: нарушение относят к сфере
    // целиком, а вид внутри сферы — необязательное уточнение (классификатор наполняется постепенно,
    // и строгое «только вид» оставляло бы учёт пустым, пока администратор не заведёт виды).
    private static async Task<ViolationWriteResult> CheckReferencesAsync(
        InspectorDbContext db, ViolationDraft draft, CancellationToken cancellationToken)
    {
        if (!await db.Divisions.AnyAsync(d => d.Id == draft.DivisionId, cancellationToken))
        {
            return ViolationWriteResult.NotFound;
        }

        return await db.ViolationCategories.AnyAsync(c => c.Id == draft.CategoryId, cancellationToken)
            ? ViolationWriteResult.Ok
            : ViolationWriteResult.NotFound;
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
