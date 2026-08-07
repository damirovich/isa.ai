using ISC.AI.Modules.DocFlow.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Modules.DocFlow.Data;

/// <summary>
/// Использование подразделений в документообороте (реализация <see cref="IDivisionUsage"/>).
/// </summary>
/// <remarks>
/// Разграничения доступа здесь НЕТ намеренно, и это не упущение. Ответ — количества по СВОЕМУ
/// справочнику подразделений, без единого признака конкретного документа: ни номера, ни содержания,
/// ни грифа. Спрашивает его администратор, ведущий справочник, и ровно затем, чтобы не удалить
/// подразделение, за которым числятся поручения. Сузь этот счёт допуском — и удаление стало бы
/// разрушительным: администратор, не видящий чужих документов, получил бы ноль и снёс подразделение,
/// к которому они привязаны. Право на сам вызов проверяет сценарий профиля.
/// </remarks>
public sealed class DivisionUsageQuery(IDbContextFactory<DocFlowDbContext> contextFactory) : IDivisionUsage
{
    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<int, DivisionUsage>> CountAsync(
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Две группировки вместо соединения: документ и назначение считаются по разным таблицам,
        // и объединять их в один запрос значило бы плодить строки перекрёстно.
        var documents = await db.Documents.AsNoTracking()
            .GroupBy(d => d.DivisionId)
            .Select(g => new { DivisionId = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var assignments = await db.DocumentAssignments.AsNoTracking()
            .GroupBy(a => a.DivisionId)
            .Select(g => new { DivisionId = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var result = new Dictionary<int, DivisionUsage>();

        foreach (var row in documents)
        {
            result[row.DivisionId] = new DivisionUsage(row.Count, 0);
        }

        foreach (var row in assignments)
        {
            var existing = result.TryGetValue(row.DivisionId, out var value) ? value : DivisionUsage.None;
            result[row.DivisionId] = existing with { Assignments = row.Count };
        }

        return result;
    }
}
