using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Domain.Entities;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ISC.AI.Profile.Investigation.Data;

/// <summary>
/// Хранилище дел поверх <c>investigation.case_file</c> (ТФ-ДЕЛ-01..03). Каждое чтение с
/// <see cref="AccessContext"/> проходит через <see cref="CaseAccessRule.Apply"/> (floor ядра → политика
/// профиля → роль/владение) на стороне БД; запись проверяет, что гриф/подразделение дела в пределах
/// допуска субъекта (ТБ-024). Роль субъекта читается через <see cref="IUserRoleStore"/> по
/// <see cref="AccessContext.NumericSubjectId"/> на каждую операцию — не кэшируется (ТБ-016).
/// </summary>
public sealed class CaseStore(
    IDbContextFactory<InvestigationDbContext> contextFactory,
    IAccessPolicy policy,
    IUserRoleStore roles) : ICaseStore
{
    private const string UniqueViolation = "23505";

    /// <inheritdoc />
    public async Task<CasePage> ListAsync(CaseFilter filter, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(access);

        var role = await ResolveRoleAsync(access, cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var query = CaseAccessRule.Apply(db.Cases.AsNoTracking(), access, policy, role);

        if (!string.IsNullOrWhiteSpace(filter.Text))
        {
            var pattern = "%" + filter.Text.Trim() + "%";
            query = query.Where(c => EF.Functions.ILike(c.Number, pattern) || EF.Functions.ILike(c.Title, pattern));
        }

        if (filter.Kind is { } kind)
        {
            query = query.Where(c => c.Kind == kind);
        }

        if (filter.Status is { } status)
        {
            query = query.Where(c => c.Status == status);
        }

        if (filter.DivisionId is { } divisionId)
        {
            query = query.Where(c => c.DivisionId == divisionId);
        }

        if (filter.InvestigatorUserId is { } investigatorUserId)
        {
            query = query.Where(c => c.InvestigatorUserId == investigatorUserId);
        }

        var total = await query.CountAsync(cancellationToken);

        var pageSize = Math.Max(1, filter.PageSize);
        var page = Math.Max(1, filter.Page);

        var rows = await query
            .OrderByDescending(c => c.OpenedAt)
            .ThenByDescending(c => c.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(c => new CaseRow(
                c.Id, c.Number, c.Title, c.Kind, c.Status, c.OpenedAt, c.InvestigatorUserId,
                c.DivisionId, c.Classification,
                db.CaseMediaLinks.Count(l => l.CaseId == c.Id),
                db.Persons.Count(p => p.CaseId == c.Id)))
            .ToListAsync(cancellationToken);

        return new CasePage(rows, total);
    }

    /// <inheritdoc />
    public async Task<CaseDetails?> GetAsync(int caseId, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);

        var role = await ResolveRoleAsync(access, cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var entity = await CaseAccessRule.Apply(db.Cases.AsNoTracking(), access, policy, role)
            .FirstOrDefaultAsync(c => c.Id == caseId, cancellationToken);
        if (entity is null)
        {
            return null;
        }

        var media = await db.CaseMediaLinks.AsNoTracking()
            .Where(l => l.CaseId == caseId)
            .OrderBy(l => l.Id)
            .Select(l => new CaseMediaLinkRow(l.MediaAssetId, l.Place, l.LinkedByUserId, l.CreatedAt))
            .ToListAsync(cancellationToken);

        var authorizations = await db.SearchAuthorizations.AsNoTracking()
            .Where(a => a.CaseId == caseId)
            .OrderByDescending(a => a.IssuedAt)
            .ThenByDescending(a => a.Id)
            .Select(a => new SearchAuthorizationRow(
                a.Id, a.CaseId, a.Kind, a.Reference, a.IssuedAt, a.IssuedByUserId, a.ValidUntil, a.Notes))
            .ToListAsync(cancellationToken);

        return new CaseDetails(
            entity.Id, entity.Number, entity.Title, entity.Kind, entity.Status, entity.OpenedAt,
            entity.InvestigatorUserId, entity.DivisionId, entity.Classification, entity.Basis,
            entity.ClosedAt, entity.CreatedAt, media, authorizations);
    }

    /// <inheritdoc />
    public async Task<(CaseWriteResult Result, int CaseId)> CreateAsync(CaseDraft draft, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(access);

        // ТБ-024: гриф и подразделение — только в пределах допуска субъекта. Дело выше собственного
        // допуска создать нельзя — иначе создатель тут же потерял бы к нему доступ, а решётка ядра
        // обошлась бы записью «вслепую».
        if (draft.Classification > access.MaxClassification || !access.AllowedDivisions.Contains(draft.DivisionId))
        {
            return (CaseWriteResult.OutsideClearance, 0);
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var number = draft.Number.Trim();
        if (await db.Cases.AnyAsync(c => c.DivisionId == draft.DivisionId && c.Number == number, cancellationToken))
        {
            return (CaseWriteResult.DuplicateNumber, 0);
        }

        var entity = new CaseFile
        {
            Number = number,
            Title = draft.Title.Trim(),
            Kind = draft.Kind,
            OpenedAt = draft.OpenedAt,
            InvestigatorUserId = draft.InvestigatorUserId,
            DivisionId = draft.DivisionId,
            Classification = draft.Classification,
            Basis = string.IsNullOrWhiteSpace(draft.Basis) ? null : draft.Basis.Trim(),
            CreatedByUserId = draft.CreatedByUserId ?? access.NumericSubjectId,
        };
        db.Cases.Add(entity);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: UniqueViolation })
        {
            // Гонка двух одновременных регистраций одного номера: предварительная проверка прошла у обоих,
            // уникальный индекс (division_id, number) остановил второго.
            return (CaseWriteResult.DuplicateNumber, 0);
        }

        return (CaseWriteResult.Ok, entity.Id);
    }

    /// <inheritdoc />
    public async Task<CaseWriteResult> UpdateAsync(
        int caseId, string title, CaseKind kind, DateOnly openedAt, int? investigatorUserId, string? basis,
        AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        var role = await ResolveRoleAsync(access, cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Нельзя менять то, чего не видишь: недоступное дело неотличимо от отсутствующего (ТБ-021).
        var entity = await CaseAccessRule.Apply(db.Cases, access, policy, role)
            .FirstOrDefaultAsync(c => c.Id == caseId, cancellationToken);
        if (entity is null)
        {
            return CaseWriteResult.NotFound;
        }

        entity.Title = title.Trim();
        entity.Kind = kind;
        entity.OpenedAt = openedAt;
        entity.InvestigatorUserId = investigatorUserId;
        entity.Basis = string.IsNullOrWhiteSpace(basis) ? null : basis.Trim();
        await db.SaveChangesAsync(cancellationToken);
        return CaseWriteResult.Ok;
    }

    /// <inheritdoc />
    public async Task<CaseWriteResult> SetStatusAsync(int caseId, CaseStatus status, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);

        var role = await ResolveRoleAsync(access, cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var entity = await CaseAccessRule.Apply(db.Cases, access, policy, role)
            .FirstOrDefaultAsync(c => c.Id == caseId, cancellationToken);
        if (entity is null)
        {
            return CaseWriteResult.NotFound;
        }

        entity.Status = status;
        entity.ClosedAt = status == CaseStatus.Closed ? DateTime.UtcNow : null;
        await db.SaveChangesAsync(cancellationToken);
        return CaseWriteResult.Ok;
    }

    /// <inheritdoc />
    public async Task<(CaseWriteResult Result, int AuthorizationId)> AddAuthorizationAsync(
        SearchAuthorizationDraft draft, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(access);
        ArgumentException.ThrowIfNullOrWhiteSpace(draft.Reference);

        var role = await ResolveRoleAsync(access, cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var visible = await CaseAccessRule.Apply(db.Cases.AsNoTracking(), access, policy, role)
            .AnyAsync(c => c.Id == draft.CaseId, cancellationToken);
        if (!visible)
        {
            return (CaseWriteResult.NotFound, 0);
        }

        var entity = new SearchAuthorization
        {
            CaseId = draft.CaseId,
            Kind = draft.Kind,
            Reference = draft.Reference.Trim(),
            IssuedAt = draft.IssuedAt,
            IssuedByUserId = draft.IssuedByUserId ?? access.NumericSubjectId,
            ValidUntil = draft.ValidUntil,
            Notes = string.IsNullOrWhiteSpace(draft.Notes) ? null : draft.Notes.Trim(),
        };
        db.SearchAuthorizations.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        return (CaseWriteResult.Ok, entity.Id);
    }

    /// <inheritdoc />
    /// <remarks>Без решётки по контракту: вызывающий (пакет «Медиа» через <c>ICaseScope</c>) доступ к делу уже проверил.</remarks>
    public async Task<CaseWriteResult> LinkMediaAsync(int caseId, int mediaAssetId, string? place, int? linkedByUserId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        if (!await db.Cases.AnyAsync(c => c.Id == caseId, cancellationToken))
        {
            return CaseWriteResult.NotFound;
        }

        if (await db.CaseMediaLinks.AnyAsync(l => l.CaseId == caseId && l.MediaAssetId == mediaAssetId, cancellationToken))
        {
            return CaseWriteResult.Ok;
        }

        db.CaseMediaLinks.Add(new CaseMediaLink
        {
            CaseId = caseId,
            MediaAssetId = mediaAssetId,
            Place = string.IsNullOrWhiteSpace(place) ? null : place.Trim(),
            LinkedByUserId = linkedByUserId,
        });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: UniqueViolation })
        {
            // Параллельная привязка того же носителя — идемпотентность обеспечивает уникальный индекс.
        }

        return CaseWriteResult.Ok;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyCollection<int>> ListMediaAssetIdsAsync(IReadOnlyCollection<int> caseIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caseIds);
        if (caseIds.Count == 0)
        {
            return [];
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var ids = caseIds.ToArray();
        return await db.CaseMediaLinks.AsNoTracking()
            .Where(l => ids.Contains(l.CaseId))
            .Select(l => l.MediaAssetId)
            .Distinct()
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int?> FindCaseByMediaAsync(int mediaAssetId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.CaseMediaLinks.AsNoTracking()
            .Where(l => l.MediaAssetId == mediaAssetId)
            .OrderBy(l => l.Id)
            .Select(l => (int?)l.CaseId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<int>> ListAccessibleIdsAsync(AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);

        var role = await ResolveRoleAsync(access, cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await CaseAccessRule.Apply(db.Cases.AsNoTracking(), access, policy, role)
            .OrderBy(c => c.Id)
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task SaveClosureActAsync(CaseClosureActDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Акт на дело один: повторное закрытие перезаписывает числа последнего исполненного регламента.
        // Хранить историю попыток незачем — полная хронология удалений и без того в журнале аудита.
        var act = await db.ClosureActs.FirstOrDefaultAsync(a => a.CaseId == draft.CaseId, cancellationToken)
            ?? db.ClosureActs.Add(new CaseClosureAct { CaseId = draft.CaseId }).Entity;

        act.ExecutedAt = DateTime.UtcNow;
        act.ExecutedByUserId = draft.ExecutedByUserId;
        act.MediaAssetsTotal = draft.MediaAssetsTotal;
        act.AssetsAffected = draft.AssetsAffected;
        act.TemplatesRemoved = draft.TemplatesRemoved;
        act.CropsRemoved = draft.CropsRemoved;

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<CaseClosureActRow?> GetClosureActAsync(
        int caseId, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);

        var role = await ResolveRoleAsync(access, cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Акт виден ровно тем, кому видно дело: отдельного правила у него нет, и придумывать его нельзя —
        // иначе акт стал бы окном в закрытое чужое дело (ТБ-020/021, ТФ-ДЕЛ-03).
        var accessible = await CaseAccessRule.Apply(db.Cases.AsNoTracking(), access, policy, role)
            .AnyAsync(c => c.Id == caseId, cancellationToken);
        if (!accessible)
        {
            return null;
        }

        return await db.ClosureActs.AsNoTracking()
            .Where(a => a.CaseId == caseId)
            .Select(a => new CaseClosureActRow(
                a.CaseId, a.ExecutedAt, a.ExecutedByUserId, a.MediaAssetsTotal, a.AssetsAffected, a.TemplatesRemoved, a.CropsRemoved))
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<InvestigationRole?> ResolveRoleAsync(AccessContext access, CancellationToken cancellationToken) =>
        access.NumericSubjectId is { } userId
            ? await roles.GetRoleAsync(userId, cancellationToken)
            : null;
}
