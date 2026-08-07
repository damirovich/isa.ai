using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Domain.Entities;
using ISC.AI.Modules.DocFlow.Domain.Enums;
using ISC.AI.Modules.DocFlow.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Modules.DocFlow.Data;

/// <summary>
/// Данные отчётов (разд. 6 ТЗ СКИД) поверх <see cref="DocFlowDbContext"/>.
/// </summary>
/// <remarks>
/// РАЗГРАНИЧЕНИЕ ПРИМЕНЯЕТСЯ В ЗАПРОСЕ (инвариант 3, ТБ-020/021) — тем же предикатом, что список
/// документов: отчёт это массовая выгрузка, и постфильтр здесь означал бы, что лишние строки уже
/// вышли из БД. Верхнего предела строк НЕТ намеренно: отчёт за период по своему подразделению —
/// это сотни строк, а молчаливое усечение выдачи хуже долгого запроса (пользователь не узнал бы,
/// что часть данных не попала в документ, и принял бы решение по неполной картине).
/// </remarks>
public sealed class ReportDataSource(
    IDbContextFactory<DocFlowDbContext> contextFactory,
    IAccessPolicy accessPolicy) : IReportDataSource
{
    /// <inheritdoc />
    public async Task<ReportData> QueryAsync(
        ReportKind kind, ReportFilter filter, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(access);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var allowedDivisions = access.AllowedDivisions;

        // ТОТ ЖЕ предикат, что у DocumentStore.VisibleDocuments — решётка гриф/подразделение плюс
        // сужающая политика профиля. Дублируется выражением, а не вызовом: у отчётов свой контекст,
        // а править правило в двух местах нельзя — поэтому оно закреплено тестом «отчёт не выдаёт
        // того, чего не выдаёт список документов».
        var visibleDocuments = db.Documents
            .Where(d => d.Classification <= access.MaxClassification
                && allowedDivisions.Contains(d.DivisionId))
            .Where(accessPolicy.BuildFilter<Document>(access));

        var query =
            from assignment in db.DocumentAssignments
            join document in visibleDocuments on assignment.DocumentId equals document.Id
            join type in db.DocumentTypes on document.TypeId equals type.Id
            select new { Assignment = assignment, Document = document, TypeName = type.Name };

        // ПЕРИОД. Для «по срокам» и «просроченных» окно применяется к СРОКУ, для остальных —
        // к дате регистрации. В СКИД период всегда шёл по дате регистрации, из-за чего отчёт
        // «по срокам исполнения» по существу не работал.
        query = kind is ReportKind.ByDeadline or ReportKind.Overdue
            ? query.Where(x => x.Assignment.Deadline != null
                && x.Assignment.Deadline >= filter.From && x.Assignment.Deadline <= filter.To)
            : query.Where(x => x.Document.RegDate >= filter.From && x.Document.RegDate <= filter.To);

        if (kind == ReportKind.Overdue)
        {
            query = query.Where(x => x.Assignment.Status == AssignmentStatus.Overdue);
        }

        if (filter.DivisionId is { } divisionId)
        {
            query = query.Where(x => x.Assignment.DivisionId == divisionId);
        }

        if (filter.AssigneeUserId is { } assigneeUserId)
        {
            query = query.Where(x => x.Assignment.AssigneeUserId == assigneeUserId);
        }

        if (filter.InspectorUserId is { } inspectorUserId)
        {
            query = query.Where(x => x.Document.InspectorUserId == inspectorUserId);
        }

        if (filter.TypeId is { } typeId)
        {
            query = query.Where(x => x.Document.TypeId == typeId);
        }

        if (filter.Status is { } status)
        {
            query = query.Where(x => x.Assignment.Status == status);
        }

        // Сортировка — по смыслу вида: у срочных отчётов первым идёт ближайший срок, у остальных
        // порядок регистрации. Внутри — стабильный доп. ключ, иначе строки «прыгают» между выгрузками.
        query = kind is ReportKind.ByDeadline or ReportKind.Overdue
            ? query.OrderBy(x => x.Assignment.Deadline).ThenBy(x => x.Assignment.Id)
            : query.OrderBy(x => x.Document.RegDate).ThenBy(x => x.Assignment.Id);

        var rows = await query
            .Select(x => new ReportRow(
                x.Document.Id,
                x.Assignment.Id,
                x.Document.RegNumber,
                x.Document.RegDate,
                x.TypeName,
                x.Document.ShortContent,
                x.Assignment.DivisionId,
                x.Assignment.AssigneeUserId,
                x.Document.InspectorUserId,
                x.Assignment.Deadline,
                x.Assignment.Status,
                x.Document.Classification))
            .ToListAsync(cancellationToken);

        // Гриф всего отчёта — НАИБОЛЬШИЙ среди попавших строк: сводка по документам ДСП сама ДСП
        // (ТБ-033 для маркировки файла, ТБ-032 для записи журнала). Пустой отчёт — гриф 0.
        var maxClassification = rows.Count == 0 ? (short)0 : rows.Max(r => r.Classification);

        return new ReportData(rows, maxClassification);
    }
}
