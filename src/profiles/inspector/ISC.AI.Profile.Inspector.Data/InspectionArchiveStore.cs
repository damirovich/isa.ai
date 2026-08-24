using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Profile.Inspector.Domain.Enums;
using ISC.AI.Profile.Inspector.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Profile.Inspector.Data;

/// <summary>
/// Архив проверок (<see cref="IInspectionArchiveStore"/>, §5.2.4) в схеме <c>inspector</c>:
/// нарушения, сгруппированные по паре «справка-проверка × подразделение». Нарушения без справки
/// образуют группу «вне проверок» своего подразделения — история не теряется из-за пустой ссылки.
/// </summary>
public sealed class InspectionArchiveStore(IDbContextFactory<InspectorDbContext> contextFactory)
    : IInspectionArchiveStore
{
    /// <summary>Предел страницы групп: архив листают, а не выгружают целиком.</summary>
    public const int MaxPageSize = 50;

    /// <inheritdoc />
    public async Task<ArchivePage> ListAsync(ArchiveFilter filter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var query = ApplyFilter(db, filter);

        var grouped = query.GroupBy(v => new
        {
            v.ReferenceDocRef,
            v.DivisionId,
            DivisionName = v.Division!.Name,
        });

        // Общее число групп — ДО среза страницы.
        var total = await grouped.CountAsync(cancellationToken);

        var page = Math.Max(1, filter.Page);
        var pageSize = Math.Clamp(filter.PageSize, 1, MaxPageSize);

        // Свежие проверки первыми (при равной дате — по id последнего нарушения: детерминированно).
        var heads = await grouped
            .Select(g => new
            {
                g.Key.ReferenceDocRef,
                g.Key.DivisionId,
                g.Key.DivisionName,
                Count = g.Count(),
                Open = g.Sum(v => v.RemediationStatus != RemediationStatus.Resolved ? 1 : 0),
                First = g.Min(v => v.DetectedAt),
                Last = g.Max(v => v.DetectedAt),
                MaxSeverity = g.Max(v => (int)v.Severity),
                LastId = g.Max(v => v.Id),
            })
            .OrderByDescending(g => g.Last).ThenByDescending(g => g.LastId)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        if (heads.Count == 0)
        {
            return new ArchivePage([], total);
        }

        // Нарушения групп страницы: ссылки и подразделения — списками, точные пары добираются
        // в памяти (ТО-инф-06-стиль: чужих комбинаций придёт немного, страница мала).
        var refs = heads.Where(h => h.ReferenceDocRef != null).Select(h => h.ReferenceDocRef!).Distinct().ToList();
        var hasNullGroup = heads.Any(h => h.ReferenceDocRef == null);
        var divisionIds = heads.Select(h => h.DivisionId).Distinct().ToList();

        var rows = await ApplyFilter(db, filter)
            .Where(v => divisionIds.Contains(v.DivisionId)
                && ((v.ReferenceDocRef != null && refs.Contains(v.ReferenceDocRef))
                    || (hasNullGroup && v.ReferenceDocRef == null)))
            .OrderByDescending(v => v.DetectedAt).ThenByDescending(v => v.Id)
            .Select(v => new
            {
                v.Id,
                v.ReferenceDocRef,
                v.DivisionId,
                CategoryName = v.Category!.Name,
                CategoryParentName = v.Category!.Parent != null ? v.Category.Parent.Name : null,
                v.Severity,
                v.DetectedAt,
                v.RemediationStatus,
            })
            .ToListAsync(cancellationToken);

        var rowsByGroup = rows
            .GroupBy(r => (r.ReferenceDocRef, r.DivisionId))
            .ToDictionary(g => g.Key, g => g.ToList());

        var groups = heads
            .Select(h =>
            {
                var groupRows = rowsByGroup.GetValueOrDefault((h.ReferenceDocRef, h.DivisionId)) ?? [];
                return new ArchiveGroup(
                    h.ReferenceDocRef,
                    h.DivisionId,
                    h.DivisionName,
                    h.Count,
                    h.Open,
                    h.First,
                    h.Last,
                    (ViolationSeverity)h.MaxSeverity,
                    [.. groupRows.Select(r => new ArchiveViolationRow(
                        r.Id, r.CategoryName, r.CategoryParentName, r.Severity, r.DetectedAt, r.RemediationStatus))]);
            })
            .ToList();

        return new ArchivePage(groups, total);
    }

    // Отбор — общий для заголовков групп и их строк, иначе сводка разойдётся с содержимым.
    private static IQueryable<Domain.Entities.Violation> ApplyFilter(InspectorDbContext db, ArchiveFilter filter)
    {
        var query = db.Violations.AsNoTracking();

        if (filter.DivisionId is { } divisionId)
        {
            query = query.Where(v => v.DivisionId == divisionId);
        }

        if (filter.CategoryId is { } categoryId)
        {
            // Выбор сферы (верхний уровень) означает все её виды — как в реестре нарушений.
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

        if (!string.IsNullOrWhiteSpace(filter.RefSearch))
        {
            // Поиск по номеру справки. Спецсимволы LIKE экранируются — «№12_3» ищется буквально.
            var pattern = "%" + filter.RefSearch.Trim()
                .Replace(@"\", @"\\")
                .Replace("%", @"\%")
                .Replace("_", @"\_") + "%";
            query = query.Where(v => v.ReferenceDocRef != null
                && EF.Functions.ILike(v.ReferenceDocRef, pattern, @"\"));
        }

        return query;
    }
}
