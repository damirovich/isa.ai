using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Persistence.Audit;

/// <summary>Чтение журнала аудита (<c>core.audit_record</c>) с разграничением по решётке (ТБ-032).</summary>
public sealed class AuditReader(IDbContextFactory<CoreDbContext> contextFactory) : IAuditReader
{
    /// <summary>Потолок размера страницы: журнал большой, и «покажи всё» его не выдержит.</summary>
    public const int MaxPageSize = 200;

    /// <inheritdoc />
    public async Task<AuditPage> QueryAsync(
        AuditFilter filter, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(access);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var allowedDivisions = access.AllowedDivisions;

        // РЕШЁТКА В ЗАПРОСЕ (ТБ-032). Записи БЕЗ подразделения (вход в систему, общесистемные
        // действия) отсекаются только грифом: у них нет владельца-подразделения, и требовать
        // попадания в список разрешённых значило бы не показывать их вообще никому.
        var query = db.AuditRecords.AsNoTracking()
            .Where(r => r.Classification <= access.MaxClassification)
            .Where(r => r.DivisionId == null || allowedDivisions.Contains(r.DivisionId.Value));

        if (filter.From is { } from)
        {
            query = query.Where(r => r.OccurredAt >= from);
        }

        if (filter.To is { } to)
        {
            query = query.Where(r => r.OccurredAt <= to);
        }

        if (filter.SubjectId is { } subjectId)
        {
            query = query.Where(r => r.SubjectId == subjectId);
        }

        if (filter.Action is { } action)
        {
            query = query.Where(r => r.Action == action);
        }

        if (!string.IsNullOrWhiteSpace(filter.ObjectRef))
        {
            var text = filter.ObjectRef.Trim();
            query = query.Where(r => r.ObjectRef != null && EF.Functions.ILike(r.ObjectRef, $"%{text}%"));
        }

        // Общее число — ДО постраничного среза: без него навигация не знает, сколько страниц.
        var total = await query.CountAsync(cancellationToken);

        var page = Math.Max(1, filter.Page);
        var pageSize = Math.Clamp(filter.PageSize, 1, MaxPageSize);

        // Имя субъекта подставляется соединением с реестром пользователей: в самой записи его нет
        // (журнал хранит идентификатор — имя может измениться, а запись неизменяема, ТБ-031).
        var rows = await query
            .OrderByDescending(r => r.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .GroupJoin(db.Users.AsNoTracking(), r => r.SubjectId, u => u.Id, (r, users) => new { r, users })
            .SelectMany(x => x.users.DefaultIfEmpty(), (x, user) => new AuditRecordRow(
                x.r.Id,
                x.r.OccurredAt,
                x.r.SubjectId,
                user == null ? null : user.DisplayName,
                x.r.Action,
                x.r.ObjectRef,
                x.r.Classification,
                x.r.DivisionId,
                x.r.PayloadSensitive))
            .ToListAsync(cancellationToken);

        return new AuditPage(rows, total);
    }
}
