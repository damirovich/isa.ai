using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Domain.Entities;
using ISC.AI.Modules.DocFlow.Domain.Enums;
using ISC.AI.Modules.DocFlow.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Modules.DocFlow.Data;

/// <summary>
/// Данные дашборда документооборота поверх <see cref="DocFlowDbContext"/>.
/// </summary>
/// <remarks>
/// РАЗГРАНИЧЕНИЕ В ЗАПРОСЕ — тем же предикатом, что список документов и отчёты (ТБ-020/021 +
/// политика профиля ADR-0014). Агрегат опаснее строки: «просрочено: 7» при пустом реестре сообщает
/// о существовании семи недоступных поручений, то есть утечка идёт числом, а не текстом.
/// </remarks>
public sealed class DashboardDataSource(
    IDbContextFactory<DocFlowDbContext> contextFactory,
    IAccessPolicy accessPolicy) : IDashboardDataSource
{
    // «В работе» выражено ОТ ПРОТИВНОГО: всё, что не завершено и не просрочено. Так, а не списком
    // статусов, по двум причинам. (1) EF не переводит Contains по массиву внутри агрегатов
    // (Count внутри GroupBy) — запрос падал бы уже на первом открытии дашборда. (2) Смысл устойчив
    // к появлению нового промежуточного статуса: он сам собой попадёт в «в работе», а не выпадет
    // из счётчиков молча. «Просрочено» вынесено отдельно намеренно — растворившись в общем числе,
    // просроченные поручения перестали бы бросаться в глаза, а весь смысл дашборда в обратном.

    /// <inheritdoc />
    public async Task<DashboardData> GetAsync(
        AccessContext access,
        DateOnly today,
        int horizonDays,
        int upcomingCount = 10,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var allowedDivisions = access.AllowedDivisions;

        // ТОТ ЖЕ предикат, что у DocumentStore.VisibleDocuments и ReportDataSource. Дублируется
        // выражением, а не вызовом (у каждого свой контекст); от расхождения страхует тест
        // «дашборд не считает того, чего не показывает список документов».
        var visibleDocuments = db.Documents
            .Where(d => d.Classification <= access.MaxClassification
                && allowedDivisions.Contains(d.DivisionId))
            .Where(accessPolicy.BuildFilter<Document>(access));

        // Доступные назначения отбираются ПОДЗАПРОСОМ по идентификаторам, а не соединением таблиц.
        // Причина техническая и обязательная: EF не переводит GroupBy с агрегатами поверх результата
        // Join — запрос падал бы прямо при открытии дашборда. Подзапрос ложится в обычный IN (...)
        // и группируется без хлопот, а смысл предиката тот же.
        var visibleDocumentIds = visibleDocuments.Select(d => d.Id);
        var visible = db.DocumentAssignments.Where(a => visibleDocumentIds.Contains(a.DocumentId));

        var horizonEnd = today.AddDays(Math.Max(horizonDays, 0));

        var all = await CountAsync(visible, today, horizonEnd, cancellationToken);

        // Личный срез: я исполнитель ЛИБО я инспектор документа. Без числового субъекта (dev-заглушка,
        // системный вызов) личного среза нет — показывать чужой было бы хуже, чем не показывать никакой.
        AssignmentCounters mine;
        if (access.NumericSubjectId is { } userId)
        {
            var myDocumentIds = visibleDocuments.Where(d => d.InspectorUserId == userId).Select(d => d.Id);
            mine = await CountAsync(
                visible.Where(a => a.AssigneeUserId == userId || myDocumentIds.Contains(a.DocumentId)),
                today, horizonEnd, cancellationToken);
        }
        else
        {
            mine = AssignmentCounters.Empty;
        }

        // ДВА ограничения EF Core, которые здесь пришлось обойти, — оба валят запрос при открытии экрана:
        // (1) условный Count внутри GroupBy не переводится → считаем Sum(… ? 1 : 0), это обычный
        //     SUM(CASE WHEN …); (2) проекция в СВОЙ тип через конструктор внутри GroupBy тоже
        //     не переводится → собираем анонимно, а в доменную запись перекладываем уже в памяти.
        var divisionRows = await visible
            .GroupBy(a => a.DivisionId)
            .Select(g => new
            {
                DivisionId = g.Key,
                Active = g.Sum(a => a.Status != AssignmentStatus.Overdue
                    && a.Status != AssignmentStatus.Done
                    && a.Status != AssignmentStatus.Closed ? 1 : 0),
                Overdue = g.Sum(a => a.Status == AssignmentStatus.Overdue ? 1 : 0),
            })
            // Сначала те, где просрочек больше: дашборд читают, чтобы понять, куда смотреть.
            .OrderByDescending(x => x.Overdue).ThenByDescending(x => x.Active)
            .ToListAsync(cancellationToken);

        var byDivision = divisionRows
            .Select(x => new DivisionLoad(x.DivisionId, x.Active, x.Overdue))
            .ToList();

        var upcoming = await visible
            .Where(a => a.Deadline != null
                && a.Deadline <= horizonEnd
                && a.Status != AssignmentStatus.Done
                && a.Status != AssignmentStatus.Closed)
            .OrderBy(a => a.Deadline)
            .Take(upcomingCount)
            .Select(a => new UpcomingDeadline(
                a.DocumentId,
                a.Id,
                a.Document!.RegNumber,
                a.Document.ShortContent,
                a.Deadline!.Value,
                a.DivisionId,
                a.AssigneeUserId,
                a.Status))
            .ToListAsync(cancellationToken);

        return new DashboardData(all, mine, byDivision, upcoming);
    }

    /// <summary>Считает все пять чисел ОДНИМ запросом, а не пятью: дашборд открывают часто.</summary>
    private static async Task<AssignmentCounters> CountAsync(
        IQueryable<DocumentAssignment> assignments,
        DateOnly today,
        DateOnly horizonEnd,
        CancellationToken cancellationToken)
    {
        var counters = await assignments
            .GroupBy(_ => 1)
            // Анонимный тип и Sum(… ? 1 : 0) — по тем же ограничениям EF Core, что у разреза
            // по подразделениям (см. пояснение выше).
            .Select(g => new
            {
                Active = g.Sum(a => a.Status != AssignmentStatus.Overdue
                    && a.Status != AssignmentStatus.Done
                    && a.Status != AssignmentStatus.Closed ? 1 : 0),
                Overdue = g.Sum(a => a.Status == AssignmentStatus.Overdue ? 1 : 0),
                DueToday = g.Sum(a => a.Deadline == today
                    && a.Status != AssignmentStatus.Overdue
                    && a.Status != AssignmentStatus.Done
                    && a.Status != AssignmentStatus.Closed ? 1 : 0),
                DueSoon = g.Sum(a => a.Deadline > today && a.Deadline <= horizonEnd
                    && a.Status != AssignmentStatus.Overdue
                    && a.Status != AssignmentStatus.Done
                    && a.Status != AssignmentStatus.Closed ? 1 : 0),
                Done = g.Sum(a => a.Status == AssignmentStatus.Done ? 1 : 0),
            })
            .FirstOrDefaultAsync(cancellationToken);

        // Пустая выборка не даёт группы вовсе — это не ошибка, а «нечего показывать».
        return counters is null
            ? AssignmentCounters.Empty
            : new AssignmentCounters(
                counters.Active, counters.Overdue, counters.DueToday, counters.DueSoon, counters.Done);
    }
}
