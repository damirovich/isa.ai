using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Modules.DocFlow.Data;

/// <summary>
/// Разрешение слабых ссылок по RegNumber (<see cref="IDocumentLookup"/>) над схемой <c>docflow</c>.
/// Решётка доступа — В ЗАПРОСЕ, тем же предикатом, что список документов (ТБ-020/021):
/// иначе чужой модуль стал бы обходным каналом чтения метаданных недоступных документов.
/// </summary>
public sealed class DocumentLookup(
    IDbContextFactory<DocFlowDbContext> contextFactory,
    IAccessPolicy accessPolicy) : IDocumentLookup
{
    /// <summary>Предел партии номеров за вызов — вызывающий работает страницами, не всей историей.</summary>
    public const int MaxBatch = 200;

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, DocumentRefCard>> ResolveByRegNumbersAsync(
        IReadOnlyCollection<string> regNumbers, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(regNumbers);
        ArgumentNullException.ThrowIfNull(access);

        var wanted = regNumbers
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Distinct(StringComparer.Ordinal)
            .Take(MaxBatch)
            .ToList();
        if (wanted.Count == 0)
        {
            return new Dictionary<string, DocumentRefCard>(StringComparer.Ordinal);
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var rows = await db.Documents.AsNoTracking()
            .VisibleTo(access, accessPolicy)
            .Where(d => d.RegNumber != null && wanted.Contains(d.RegNumber))
            .Select(d => new
            {
                d.Id,
                RegNumber = d.RegNumber!,
                d.RegDate,
                TypeName = d.Type!.Name,
                d.InspectorUserId,
                d.AggregatedStatus,
                d.DivisionId,
            })
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(
            r => r.RegNumber,
            r => new DocumentRefCard(
                r.Id, r.RegNumber, r.RegDate, r.TypeName, r.InspectorUserId, r.AggregatedStatus, r.DivisionId),
            StringComparer.Ordinal);
    }
}
