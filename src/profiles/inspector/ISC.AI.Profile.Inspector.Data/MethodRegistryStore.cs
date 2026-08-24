using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Persistence;
using ISC.AI.Profile.Inspector.Domain.Entities;
using ISC.AI.Profile.Inspector.Domain.Enums;
using ISC.AI.Profile.Inspector.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Profile.Inspector.Data;

/// <summary>
/// Реестр методик (<see cref="IMethodRegistryStore"/>, §5.2.9 / Ц-03) в схеме <c>inspector</c>.
/// РЕЖИМ: все выборки и правки фильтруются по допуску В ЗАПРОСЕ (гриф ≤ допуска, ТБ-020-стиль) —
/// методика выше допуска неотличима от несуществующей. Имена «кто сохранил/утвердил» — из реестра
/// пользователей ядра одним запросом, склейка в памяти (ТО-инф-06).
/// </summary>
public sealed class MethodRegistryStore(
    IDbContextFactory<InspectorDbContext> contextFactory,
    IDbContextFactory<CoreDbContext> coreContextFactory) : IMethodRegistryStore
{
    /// <summary>Предел страницы — реестр листают, а не выгружают целиком.</summary>
    public const int MaxPageSize = 50;

    /// <inheritdoc />
    public async Task<int> SaveAsync(MethodDocumentDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var document = new MethodDocument
        {
            ArtifactKind = draft.ArtifactKind.Trim(),
            InspectionType = draft.InspectionType.Trim(),
            Scope = draft.Scope.Trim(),
            Body = draft.Body,
            CitationsJson = draft.CitationsJson,
            AllCitationsConfirmed = draft.AllCitationsConfirmed,
            Classification = draft.Classification,
            Status = MethodDocumentStatus.Draft, // сохранённое — всегда черновик: утверждает человек отдельно.
            CreatedByUserId = draft.CreatedByUserId,
        };
        db.MethodDocuments.Add(document);
        await db.SaveChangesAsync(cancellationToken);
        return document.Id;
    }

    /// <inheritdoc />
    public async Task<MethodPage> ListAsync(
        MethodListFilter filter, short maxClassification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var query = db.MethodDocuments.AsNoTracking()
            .Where(m => m.Classification <= maxClassification);

        if (!string.IsNullOrWhiteSpace(filter.ArtifactKind))
        {
            query = query.Where(m => m.ArtifactKind == filter.ArtifactKind);
        }

        if (filter.Status is { } status)
        {
            query = query.Where(m => m.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            // Поиск по объекту и типу проверки; спецсимволы LIKE экранируются.
            var pattern = "%" + filter.Search.Trim()
                .Replace(@"\", @"\\")
                .Replace("%", @"\%")
                .Replace("_", @"\_") + "%";
            query = query.Where(m => EF.Functions.ILike(m.Scope, pattern, @"\")
                || EF.Functions.ILike(m.InspectionType, pattern, @"\"));
        }

        var total = await query.CountAsync(cancellationToken);

        var page = Math.Max(1, filter.Page);
        var pageSize = Math.Clamp(filter.PageSize, 1, MaxPageSize);

        var rows = await query
            .OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(m => new
            {
                m.Id, m.ArtifactKind, m.InspectionType, m.Scope, m.Status,
                m.AllCitationsConfirmed, m.Classification, m.CreatedAt, m.CreatedByUserId,
            })
            .ToListAsync(cancellationToken);

        var names = await ResolveNamesAsync(
            rows.Where(r => r.CreatedByUserId is not null).Select(r => r.CreatedByUserId!.Value),
            cancellationToken);

        return new MethodPage(
            [.. rows.Select(r => new MethodDocumentListItem(
                r.Id, r.ArtifactKind, r.InspectionType, r.Scope, r.Status,
                r.AllCitationsConfirmed, r.Classification, r.CreatedAt,
                r.CreatedByUserId is { } createdBy ? names.GetValueOrDefault(createdBy) : null))],
            total);
    }

    /// <inheritdoc />
    public async Task<MethodDocumentDetails?> GetAsync(
        int methodId, short maxClassification, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.MethodDocuments.AsNoTracking()
            .Where(m => m.Id == methodId && m.Classification <= maxClassification)
            .Select(m => new
            {
                m.Id, m.ArtifactKind, m.InspectionType, m.Scope, m.Body, m.CitationsJson,
                m.AllCitationsConfirmed, m.Classification, m.Status, m.CreatedAt,
                m.CreatedByUserId, m.ApprovedByUserId,
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return null;
        }

        var ids = new List<int>();
        if (row.CreatedByUserId is { } createdBy)
        {
            ids.Add(createdBy);
        }

        if (row.ApprovedByUserId is { } approvedBy)
        {
            ids.Add(approvedBy);
        }

        var names = await ResolveNamesAsync(ids, cancellationToken);
        return new MethodDocumentDetails(
            row.Id, row.ArtifactKind, row.InspectionType, row.Scope, row.Body, row.CitationsJson,
            row.AllCitationsConfirmed, row.Classification, row.Status, row.CreatedAt,
            row.CreatedByUserId is { } c ? names.GetValueOrDefault(c) : null,
            row.ApprovedByUserId is { } a ? names.GetValueOrDefault(a) : null);
    }

    /// <inheritdoc />
    public async Task<MethodWriteResult> UpdateBodyAsync(
        int methodId, string body, short maxClassification, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(body);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var document = await db.MethodDocuments
            .FirstOrDefaultAsync(m => m.Id == methodId && m.Classification <= maxClassification, cancellationToken);
        if (document is null)
        {
            return MethodWriteResult.NotFound;
        }

        document.Body = body;
        // Правка возвращает в черновики: утверждение относится к конкретной редакции текста.
        document.Status = MethodDocumentStatus.Draft;
        document.ApprovedByUserId = null;
        await db.SaveChangesAsync(cancellationToken);
        return MethodWriteResult.Ok;
    }

    /// <inheritdoc />
    public async Task<MethodWriteResult> SetStatusAsync(
        int methodId, MethodDocumentStatus status, int? approvedByUserId, short maxClassification,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var document = await db.MethodDocuments
            .FirstOrDefaultAsync(m => m.Id == methodId && m.Classification <= maxClassification, cancellationToken);
        if (document is null)
        {
            return MethodWriteResult.NotFound;
        }

        document.Status = status;
        document.ApprovedByUserId = status == MethodDocumentStatus.Approved ? approvedByUserId : null;
        await db.SaveChangesAsync(cancellationToken);
        return MethodWriteResult.Ok;
    }

    /// <inheritdoc />
    public async Task<MethodWriteResult> DeleteAsync(
        int methodId, short maxClassification, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var document = await db.MethodDocuments
            .FirstOrDefaultAsync(m => m.Id == methodId && m.Classification <= maxClassification, cancellationToken);
        if (document is null)
        {
            return MethodWriteResult.NotFound;
        }

        if (document.Status != MethodDocumentStatus.Draft)
        {
            // Утверждённая — часть институциональной памяти; выводится правкой/возвратом в черновик, не удалением.
            return MethodWriteResult.NotDraft;
        }

        db.MethodDocuments.Remove(document);
        await db.SaveChangesAsync(cancellationToken);
        return MethodWriteResult.Ok;
    }

    private async Task<Dictionary<int, string>> ResolveNamesAsync(
        IEnumerable<int> userIds, CancellationToken cancellationToken)
    {
        var ids = userIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        await using var core = await coreContextFactory.CreateDbContextAsync(cancellationToken);
        return await core.Users.AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .Select(u => new { u.Id, Name = u.DisplayName ?? u.UserName })
            .ToDictionaryAsync(u => u.Id, u => u.Name, cancellationToken);
    }
}
