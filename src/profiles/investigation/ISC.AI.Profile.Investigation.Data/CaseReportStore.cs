using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Domain.Entities;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Profile.Investigation.Data;

/// <summary>
/// Сводки и справки по бланку поверх <c>investigation.case_report</c>, <c>case_report_revision</c> и
/// <c>case_report_permit</c> (ТФ-ДДЛ-04/05, ТФ-АДМ-06, ADR-0031).
/// </summary>
/// <remarks>
/// ИНВАРИАНТЫ:
/// <list type="bullet">
/// <item>документ виден ⇔ видно его дело по ПОЛНОЙ решётке (<see cref="CaseAccessRule.Apply"/>) и сама строка
/// проходит floor ядра и политику профиля; недоступное неотличимо от отсутствующего (ТБ-020/021);</item>
/// <item>«архивный» вычисляется здесь при каждом чтении и записи из <c>created_at</c> и окна
/// (<see cref="CaseReportOptions"/>) по <see cref="TimeProvider"/> — без фонового планировщика (ADR-0031 п. 4);</item>
/// <item>редакции только ДОБАВЛЯЮТСЯ; правка после окна — только по действующему разрешению самого субъекта,
/// его причина пишется в редакцию (ТФ-ДДЛ-05);</item>
/// <item>очередь запросов отдаёт только метаданные — ни полей бланка, ни имени объекта (ТБ-079).</item>
/// </list>
/// </remarks>
public sealed class CaseReportStore(
    IDbContextFactory<InvestigationDbContext> contextFactory,
    IAccessPolicy policy,
    IUserRoleStore roles,
    CaseReportOptions options,
    TimeProvider time) : ICaseReportStore
{
    // Очередь запросов Администратора на экране — последние N; больше за раз не разбирают.
    private const int PermitListLimit = 500;

    /// <inheritdoc />
    public async Task<IReadOnlyList<CaseReportRow>?> ListByCaseAsync(
        int caseId, string? text, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);

        var role = await ResolveRoleAsync(access, cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        if (!await CaseAccessRule.Apply(db.Cases.AsNoTracking(), access, policy, role).AnyAsync(c => c.Id == caseId, cancellationToken))
        {
            return null;
        }

        var query = Visible(db, access, role, tracking: false).Where(r => r.CaseId == caseId);
        var needle = RequisiteNormalizer.SearchText(text);
        if (needle.Length > 0)
        {
            query = query.Where(r => r.SearchText.Contains(needle));
        }

        var rows = await query
            .OrderByDescending(r => r.ReportDate).ThenByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id)
            .Select(r => new
            {
                r.Id, r.CaseId, r.Kind, r.ReportDate, r.PersonId,
                PersonName = db.Persons.Where(p => p.Id == r.PersonId).Select(p => p.DisplayName).FirstOrDefault(),
                r.IsActive, r.CreatedAt, r.CreatedByUserId, r.CurrentRevision,
            })
            .ToListAsync(cancellationToken);

        var now = Now();
        return rows
            .Select(r => new CaseReportRow(
                r.Id, r.CaseId, r.Kind, r.ReportDate, r.PersonId, r.PersonName,
                options.StateAt(AsUtc(r.CreatedAt), r.IsActive, now), AsUtc(r.CreatedAt), r.CreatedByUserId, r.CurrentRevision))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<CaseReportDetails?> GetAsync(int reportId, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);

        var role = await ResolveRoleAsync(access, cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var head = await Visible(db, access, role, tracking: false)
            .Where(r => r.Id == reportId)
            .Select(r => new
            {
                r.Id, r.CaseId, r.Kind, r.ReportDate, r.PersonId,
                PersonName = db.Persons.Where(p => p.Id == r.PersonId).Select(p => p.DisplayName).FirstOrDefault(),
                r.IsActive, r.CreatedAt, r.CreatedByUserId, r.CurrentRevision, r.Classification,
                CaseNumber = r.Case!.Number,
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (head is null)
        {
            return null;
        }

        var revisions = await Revisions(db, access)
            .Where(v => v.ReportId == reportId)
            .OrderByDescending(v => v.Number)
            .Select(v => new { v.Number, v.AuthorUserId, v.CreatedAt, v.EditReason, v.ContentJson })
            .ToListAsync(cancellationToken);
        var current = revisions.FirstOrDefault(v => v.Number == head.CurrentRevision);

        var now = Now();
        var createdAt = AsUtc(head.CreatedAt);
        var state = options.StateAt(createdAt, head.IsActive, now);

        CaseReportPermitRow? myPermit = null;
        if (access.NumericSubjectId is { } me)
        {
            myPermit = await ToRows(Permits(db, access)
                    .Where(p => p.ReportId == reportId && p.RequestedByUserId == me)
                    .OrderByDescending(p => p.RequestedAt).ThenByDescending(p => p.Id))
                .FirstOrDefaultAsync(cancellationToken);
        }

        var canEdit = state == CaseReportState.Editable
            || (state == CaseReportState.Archived && myPermit is { Status: ReportEditPermitStatus.Approved, ExpiresAt: { } until } && AsUtc(until) > now);

        var row = new CaseReportRow(
            head.Id, head.CaseId, head.Kind, head.ReportDate, head.PersonId, head.PersonName, state, createdAt,
            head.CreatedByUserId, head.CurrentRevision);
        return new CaseReportDetails(
            row,
            head.CaseNumber,
            head.Classification,
            createdAt + options.EditWindow,
            CaseReportContent.FromJson(current?.ContentJson),
            revisions.Select(v => new CaseReportRevisionRow(v.Number, v.AuthorUserId, AsUtc(v.CreatedAt), v.EditReason)).ToList(),
            myPermit is null ? null : myPermit with
            {
                RequestedAt = AsUtc(myPermit.RequestedAt),
                DecidedAt = myPermit.DecidedAt is { } d ? AsUtc(d) : null,
                ExpiresAt = myPermit.ExpiresAt is { } e ? AsUtc(e) : null,
            },
            canEdit);
    }

    /// <inheritdoc />
    public async Task<CaseReportContent?> GetRevisionAsync(
        int reportId, int number, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);

        var role = await ResolveRoleAsync(access, cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var visible = Visible(db, access, role, tracking: false);
        var json = await Revisions(db, access)
            .Where(v => v.ReportId == reportId && v.Number == number && visible.Any(r => r.Id == v.ReportId))
            .Select(v => v.ContentJson)
            .FirstOrDefaultAsync(cancellationToken);
        return json is null ? null : CaseReportContent.FromJson(json);
    }

    /// <inheritdoc />
    public async Task<(CaseReportWriteResult Result, int ReportId)> CreateAsync(
        CaseReportDraft draft, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(access);

        var role = await ResolveRoleAsync(access, cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var owner = await CaseAccessRule.Apply(db.Cases.AsNoTracking(), access, policy, role)
            .Where(c => c.Id == draft.CaseId)
            .Select(c => new { c.Id, c.Classification, c.DivisionId })
            .FirstOrDefaultAsync(cancellationToken);
        if (owner is null)
        {
            return (CaseReportWriteResult.NotFound, 0);
        }

        // Объект — фигурант ЭТОГО дела, доступный субъекту: чужой фигурант неотличим от несуществующего.
        if (draft.PersonId is { } personId
            && !await PersonAccess.Accessible(db, access, policy, role).AnyAsync(p => p.Id == personId && p.CaseId == owner.Id, cancellationToken))
        {
            return (CaseReportWriteResult.InvalidPerson, 0);
        }

        var content = draft.Content.Normalize(draft.Kind);
        var report = new CaseReport
        {
            CaseId = owner.Id,
            PersonId = draft.PersonId,
            Kind = draft.Kind,
            ReportDate = draft.ReportDate,
            CreatedByUserId = access.NumericSubjectId,
            CurrentRevision = 1,
            SearchText = content.SearchText(draft.Kind),
            Classification = owner.Classification,
            DivisionId = owner.DivisionId,
        };
        db.CaseReports.Add(report);
        db.CaseReportRevisions.Add(new CaseReportRevision
        {
            Report = report,
            Number = 1,
            ContentJson = content.ToJson(),
            AuthorUserId = access.NumericSubjectId,
            Classification = owner.Classification,
            DivisionId = owner.DivisionId,
        });

        await db.SaveChangesAsync(cancellationToken);
        return (CaseReportWriteResult.Ok, report.Id);
    }

    /// <inheritdoc />
    public async Task<CaseReportWriteResult> UpdateAsync(
        int reportId, int expectedRevision, CaseReportContent content, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(access);

        var role = await ResolveRoleAsync(access, cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var report = await Visible(db, access, role, tracking: true).FirstOrDefaultAsync(r => r.Id == reportId, cancellationToken);
        if (report is null)
        {
            return CaseReportWriteResult.NotFound;
        }

        if (!report.IsActive)
        {
            return CaseReportWriteResult.Inactive;
        }

        // ИНВАРИАНТ (ТФ-ДДЛ-05, ADR-0031 п. 5): после окна — только по ДЕЙСТВУЮЩЕМУ разрешению самого субъекта.
        var now = Now();
        string? reason = null;
        int? permitId = null;
        if (options.StateAt(AsUtc(report.CreatedAt), report.IsActive, now) == CaseReportState.Archived)
        {
            var permit = access.NumericSubjectId is { } me
                ? await db.CaseReportPermits.AsNoTracking()
                    .Where(p => p.ReportId == reportId && p.RequestedByUserId == me
                        && p.Status == ReportEditPermitStatus.Approved && p.ExpiresAt > now)
                    .OrderByDescending(p => p.ExpiresAt)
                    .Select(p => new { p.Id, p.Reason })
                    .FirstOrDefaultAsync(cancellationToken)
                : null;
            if (permit is null)
            {
                return CaseReportWriteResult.WindowClosed;
            }

            reason = permit.Reason;
            permitId = permit.Id;
        }

        // Конкурентная правка (ADR-0031 п. 6): правка шла от устаревшей редакции — не затираем чужую.
        if (report.CurrentRevision != expectedRevision)
        {
            return CaseReportWriteResult.Stale;
        }

        var normalized = content.Normalize(report.Kind);
        var json = normalized.ToJson();
        var currentJson = await db.CaseReportRevisions.AsNoTracking()
            .Where(v => v.ReportId == reportId && v.Number == report.CurrentRevision)
            .Select(v => v.ContentJson)
            .FirstOrDefaultAsync(cancellationToken);
        if (currentJson is not null && CaseReportContent.FromJson(currentJson).ToJson() == json)
        {
            return CaseReportWriteResult.Ok; // ничего не изменилось — пустую редакцию не плодим
        }

        var number = report.CurrentRevision + 1;
        db.CaseReportRevisions.Add(new CaseReportRevision
        {
            ReportId = report.Id,
            Number = number,
            ContentJson = json,
            AuthorUserId = access.NumericSubjectId,
            EditReason = reason,
            PermitId = permitId,
            Classification = report.Classification,
            DivisionId = report.DivisionId,
        });
        report.CurrentRevision = number;
        report.SearchText = normalized.SearchText(report.Kind);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            // Гонка двух одновременных правок: уникальный индекс (report_id, number) — последний рубеж.
            return CaseReportWriteResult.Stale;
        }

        return CaseReportWriteResult.Ok;
    }

    /// <inheritdoc />
    public async Task<CaseReportWriteResult> SetActiveAsync(
        int reportId, bool active, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);

        var role = await ResolveRoleAsync(access, cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var report = await Visible(db, access, role, tracking: true).FirstOrDefaultAsync(r => r.Id == reportId, cancellationToken);
        if (report is null)
        {
            return CaseReportWriteResult.NotFound;
        }

        report.IsActive = active;
        await db.SaveChangesAsync(cancellationToken);
        return CaseReportWriteResult.Ok;
    }

    /// <inheritdoc />
    public async Task<CaseReportWriteResult> RequestPermitAsync(
        int reportId, string reason, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentNullException.ThrowIfNull(access);
        if (access.NumericSubjectId is not { } me)
        {
            return CaseReportWriteResult.NotFound;
        }

        var role = await ResolveRoleAsync(access, cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var report = await Visible(db, access, role, tracking: false)
            .Where(r => r.Id == reportId)
            .Select(r => new { r.Id, r.IsActive, r.CreatedAt, r.Classification, r.DivisionId })
            .FirstOrDefaultAsync(cancellationToken);
        if (report is null)
        {
            return CaseReportWriteResult.NotFound;
        }

        if (!report.IsActive)
        {
            return CaseReportWriteResult.Inactive;
        }

        var now = Now();
        var open = await db.CaseReportPermits.AsNoTracking().AnyAsync(
            p => p.ReportId == reportId && p.RequestedByUserId == me
                && (p.Status == ReportEditPermitStatus.Pending || (p.Status == ReportEditPermitStatus.Approved && p.ExpiresAt > now)),
            cancellationToken);
        if (options.StateAt(AsUtc(report.CreatedAt), true, now) != CaseReportState.Archived || open)
        {
            return CaseReportWriteResult.PermitNotNeeded;
        }

        db.CaseReportPermits.Add(new CaseReportPermit
        {
            ReportId = report.Id,
            RequestedByUserId = me,
            Reason = reason.Trim(),
            RequestedAt = now,
            Classification = report.Classification,
            DivisionId = report.DivisionId,
        });
        await db.SaveChangesAsync(cancellationToken);
        return CaseReportWriteResult.Ok;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CaseReportPermitRow>> ListPermitsAsync(
        bool pendingOnly, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var query = Permits(db, access);
        if (pendingOnly)
        {
            query = query.Where(p => p.Status == ReportEditPermitStatus.Pending);
        }

        var rows = await ToRows(query
                .OrderBy(p => p.Status == ReportEditPermitStatus.Pending ? 0 : 1)
                .ThenByDescending(p => p.RequestedAt).ThenByDescending(p => p.Id)
                .Take(PermitListLimit))
            .ToListAsync(cancellationToken);
        return rows
            .Select(p => p with
            {
                RequestedAt = AsUtc(p.RequestedAt),
                DecidedAt = p.DecidedAt is { } d ? AsUtc(d) : null,
                ExpiresAt = p.ExpiresAt is { } e ? AsUtc(e) : null,
            })
            .ToList();
    }

    /// <inheritdoc />
    public async Task<CaseReportWriteResult> DecidePermitAsync(
        int permitId, bool approve, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var permit = await db.CaseReportPermits
            .Where(BaselineAccess.Filter<CaseReportPermit>(access))
            .Where(policy.BuildFilter<CaseReportPermit>(access))
            .FirstOrDefaultAsync(p => p.Id == permitId, cancellationToken);
        if (permit is null)
        {
            return CaseReportWriteResult.NotFound;
        }

        if (permit.Status != ReportEditPermitStatus.Pending)
        {
            return CaseReportWriteResult.AlreadyDecided;
        }

        // Правило двух лиц: разрешение на правку себе самому не выдаётся.
        if (permit.RequestedByUserId == access.NumericSubjectId)
        {
            return CaseReportWriteResult.SelfDecision;
        }

        var now = Now();
        permit.Status = approve ? ReportEditPermitStatus.Approved : ReportEditPermitStatus.Rejected;
        permit.DecidedByUserId = access.NumericSubjectId;
        permit.DecidedAt = now;
        permit.ExpiresAt = approve ? now + options.PermitDuration : null;

        await db.SaveChangesAsync(cancellationToken);
        return CaseReportWriteResult.Ok;
    }

    // --- решётка ---

    // Документы, видимые субъекту: floor + политика на строке документа, полная решётка на деле. Подзапрос по
    // делам НАРОЧНО без AsNoTracking (см. PersonAccess): режим отслеживания задаётся только на корне.
    private IQueryable<CaseReport> Visible(InvestigationDbContext db, AccessContext access, InvestigationRole? role, bool tracking)
    {
        var cases = CaseAccessRule.Apply(db.Cases, access, policy, role);
        var reports = tracking ? db.CaseReports : db.CaseReports.AsNoTracking();
        return reports
            .Where(BaselineAccess.Filter<CaseReport>(access))
            .Where(policy.BuildFilter<CaseReport>(access))
            .Where(r => cases.Any(c => c.Id == r.CaseId));
    }

    private IQueryable<CaseReportRevision> Revisions(InvestigationDbContext db, AccessContext access) =>
        db.CaseReportRevisions.AsNoTracking()
            .Where(BaselineAccess.Filter<CaseReportRevision>(access))
            .Where(policy.BuildFilter<CaseReportRevision>(access));

    private IQueryable<CaseReportPermit> Permits(InvestigationDbContext db, AccessContext access) =>
        db.CaseReportPermits.AsNoTracking()
            .Where(BaselineAccess.Filter<CaseReportPermit>(access))
            .Where(policy.BuildFilter<CaseReportPermit>(access));

    // Метаданные запросов (ТБ-079): только шапочные поля документа и номер дела — не бланк и не объект.
    // Проекция — ПОСЛЕДНИМ шагом: условия и сортировку по записи-результату EF в SQL не переводит.
    private static IQueryable<CaseReportPermitRow> ToRows(IQueryable<CaseReportPermit> permits) =>
        permits.Select(p => new CaseReportPermitRow(
                p.Id, p.ReportId, p.Report!.Kind, p.Report.ReportDate, p.Report.CaseId, p.Report.Case!.Number,
                p.RequestedByUserId, p.Reason, p.RequestedAt, p.Status, p.DecidedByUserId, p.DecidedAt, p.ExpiresAt));

    private DateTime Now() => time.GetUtcNow().UtcDateTime;

    // Npgsql читает timestamptz как Utc, но страхуемся: сравнения окна — только в UTC.
    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    private async Task<InvestigationRole?> ResolveRoleAsync(AccessContext access, CancellationToken cancellationToken) =>
        access.NumericSubjectId is { } userId
            ? await roles.GetRoleAsync(userId, cancellationToken)
            : null;
}
