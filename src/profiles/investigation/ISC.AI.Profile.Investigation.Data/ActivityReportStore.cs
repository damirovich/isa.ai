using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Domain.Entities;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Profile.Investigation.Data;

/// <summary>
/// Данные журнала суточной активности и статистики карточек объектов (реализация <see cref="IActivityReportStore"/>,
/// ADR-0061): дела — через <see cref="CaseAccessRule.Apply"/>, сводки — ещё и под своей решёткой, фигуранты — через
/// <see cref="PersonAccess"/>.
/// </summary>
public sealed class ActivityReportStore(
    IDbContextFactory<InvestigationDbContext> contextFactory,
    IAccessPolicy policy,
    IUserRoleStore roles) : IActivityReportStore
{
    /// <inheritdoc />
    public async Task<ActivityEntries> ListActivityAsync(
        DateTime fromUtc, DateTime toUtc, IReadOnlyCollection<int>? divisionIds, int limit, AccessContext access,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);

        var role = await PermissionRule.ResolveCaseViewerAsync(roles, access.NumericSubjectId, cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var cases = VisibleCases(db, access, role, divisionIds);
        var take = limit + 1;

        var media = await (
                from l in db.CaseMediaLinks.AsNoTracking()
                join c in cases on l.CaseId equals c.Id
                where l.CreatedAt >= fromUtc && l.CreatedAt < toUtc
                orderby l.CreatedAt, l.Id
                select new ActivityEntry(
                    l.CreatedAt, ActivityMaterial.Media, c.Id, c.Number, c.TaskNumber, c.DivisionId, c.Classification,
                    l.LinkedByUserId, l.MediaAssetId, null, null, true, null, null))
            .Take(take)
            .ToListAsync(cancellationToken);

        var documents = await (
                from l in db.CaseDocumentLinks.AsNoTracking()
                join c in cases on l.CaseId equals c.Id
                where l.CreatedAt >= fromUtc && l.CreatedAt < toUtc
                orderby l.CreatedAt, l.Id
                select new ActivityEntry(
                    l.CreatedAt, ActivityMaterial.Document, c.Id, c.Number, c.TaskNumber, c.DivisionId, c.Classification,
                    l.LinkedByUserId, null, l.DocFlowDocumentId, null, true, null, null))
            .Take(take)
            .ToListAsync(cancellationToken);

        // Сводки и справки — ещё и под собственной решёткой (гриф сводки может быть выше грифа дела).
        var reports = await (
                from r in db.CaseReports.AsNoTracking().Where(BaselineAccess.Filter<CaseReport>(access)).Where(policy.BuildFilter<CaseReport>(access))
                join c in cases on r.CaseId equals c.Id
                where r.CreatedAt >= fromUtc && r.CreatedAt < toUtc
                orderby r.CreatedAt, r.Id
                select new ActivityEntry(
                    r.CreatedAt, ActivityMaterial.Report, c.Id, c.Number, c.TaskNumber, c.DivisionId, c.Classification,
                    r.CreatedByUserId, null, null, r.Kind, r.IsActive, r.Classification, r.PersonId))
            .Take(take)
            .ToListAsync(cancellationToken);

        var all = media.Concat(documents).Concat(reports)
            .Select(e => e with { OccurredAtUtc = DateTime.SpecifyKind(e.OccurredAtUtc, DateTimeKind.Utc) })
            .OrderBy(e => e.OccurredAtUtc)
            .ToList();
        var truncated = all.Count > limit;
        var entries = all.Take(limit).ToList();

        // Подписи объектов: фигуранты с ролью «объект» в делах записей и фигуранты сводок — только доступные.
        var caseIds = entries.Select(e => e.CaseId).Distinct().ToList();
        var personIds = entries.Where(e => e.PersonId is not null).Select(e => e.PersonId!.Value).Distinct().ToList();
        var people = await PersonAccess.Accessible(db, access, policy, role)
            .Where(p => (caseIds.Contains(p.CaseId) && p.Role == PersonRole.Target) || personIds.Contains(p.Id))
            .Select(p => new { p.Id, p.CaseId, p.Role, p.Alias, p.DisplayName })
            .ToListAsync(cancellationToken);

        var caseObjects = people
            .Where(p => p.Role == PersonRole.Target && caseIds.Contains(p.CaseId))
            .GroupBy(p => p.CaseId)
            .ToDictionary(g => g.Key, g => string.Join(", ", g.OrderBy(p => p.Id).Select(p => Label(p.Alias, p.DisplayName))));
        var persons = people
            .Where(p => personIds.Contains(p.Id))
            .ToDictionary(p => p.Id, p => Label(p.Alias, p.DisplayName));

        return new ActivityEntries(entries, caseObjects, persons, truncated);
    }

    /// <inheritdoc />
    public async Task<ObjectCardCounts> CountObjectCardsAsync(
        DateTime fromUtc, DateTime toUtc, IReadOnlyCollection<int>? divisionIds, AccessContext access,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);

        var role = await PermissionRule.ResolveCaseViewerAsync(roles, access.NumericSubjectId, cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var groups = await VisibleCases(db, access, role, divisionIds)
            .Where(c => c.Kind == CaseKind.ObjectTask && c.CreatedAt >= fromUtc && c.CreatedAt < toUtc)
            .GroupBy(c => c.InitiatorUnitId)
            .Select(g => new
            {
                InitiatorUnitId = g.Key,
                Total = g.Count(),
                InProgress = g.Count(c => c.Status == CaseStatus.InProgress),
                Suspended = g.Count(c => c.Status == CaseStatus.Suspended),
                Closed = g.Count(c => c.Status == CaseStatus.Closed),
                MaxClassification = g.Max(c => c.Classification),
            })
            .ToListAsync(cancellationToken);

        return new ObjectCardCounts(
            groups.Select(g => new ObjectCardCount(g.InitiatorUnitId, g.Total, g.InProgress, g.Suspended, g.Closed)).ToList(),
            groups.Select(g => g.MaxClassification).DefaultIfEmpty((short)0).Max());
    }

    // Дела субъекта: то же правило, что реестр (ТБ-020/021/071), и отбор по группе — сужение внутри допуска.
    private IQueryable<CaseFile> VisibleCases(
        InvestigationDbContext db, AccessContext access, InvestigationRole? role, IReadOnlyCollection<int>? divisionIds)
    {
        var cases = CaseAccessRule.Apply(db.Cases.AsNoTracking(), access, policy, role);
        if (divisionIds is not null)
        {
            var ids = divisionIds.ToList();
            cases = cases.Where(c => ids.Contains(c.DivisionId));
        }

        return cases;
    }

    private static string Label(string? alias, string displayName) =>
        string.IsNullOrWhiteSpace(alias) ? displayName : alias.Trim();
}
