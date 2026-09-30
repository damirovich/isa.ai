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
            // № задания (ТФ-ДЕЛ-05) ищется тем же полем: оператор знает задание по номеру инициатора.
            query = query.Where(c => EF.Functions.ILike(c.Number, pattern) || EF.Functions.ILike(c.Title, pattern)
                || (c.TaskNumber != null && EF.Functions.ILike(c.TaskNumber, pattern)));
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
                db.Persons.Count(p => p.CaseId == c.Id),
                c.TaskNumber,
                c.InitiatorUnitId))
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
            entity.ClosedAt, entity.CreatedAt, media, authorizations, ToTaskRequisites(entity));
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

        // ТФ-ДЕЛ-05: реквизиты задания — ровно у задания и со ссылками на действующие записи справочников.
        if (!await IsTaskConsistentAsync(db, draft.Kind, draft.TaskRequisites, current: null, cancellationToken))
        {
            return (CaseWriteResult.InvalidTask, 0);
        }

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
        ApplyTaskRequisites(entity, draft.TaskRequisites);
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
        TaskRequisites? task, AccessContext access, CancellationToken cancellationToken = default)
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

        // Сверка со справочником — относительно ТЕКУЩИХ значений дела: выключенный после заведения ГУ
        // не мешает поправить обоснование старого задания, но выбрать его заново нельзя.
        if (!await IsTaskConsistentAsync(db, kind, task, entity, cancellationToken))
        {
            return CaseWriteResult.InvalidTask;
        }

        entity.Title = title.Trim();
        entity.Kind = kind;
        entity.OpenedAt = openedAt;
        entity.InvestigatorUserId = investigatorUserId;
        entity.Basis = string.IsNullOrWhiteSpace(basis) ? null : basis.Trim();

        // Замена целиком: при смене вида с задания на другой реквизиты задания очищаются, иначе они
        // остались бы в деле невидимыми для формы (и нарушили бы ограничение таблицы).
        ApplyTaskRequisites(entity, task);
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

    /// <inheritdoc />
    public async Task<CaseComposition?> GetCompositionAsync(
        int caseId, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);

        var role = await ResolveRoleAsync(access, cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var head = await CaseAccessRule.Apply(db.Cases.AsNoTracking(), access, policy, role)
            .Where(c => c.Id == caseId)
            .Select(c => new { c.Number, c.Classification, c.DivisionId })
            .FirstOrDefaultAsync(cancellationToken);
        if (head is null)
        {
            return null;
        }

        // Носитель ОБЩИЙ, если после дедупликации по хешу (ТФ-МЕД-04) он привязан и к другому делу: такой
        // уничтожать нельзя — он материал соседнего дела. Считается по ВСЕМ привязкам, без решётки: иначе
        // носитель чужого (недоступного субъекту) дела сошёл бы за «исключительный» и был бы стёрт.
        var assets = await db.CaseMediaLinks.AsNoTracking()
            .Where(l => l.CaseId == caseId)
            .Select(l => new
            {
                l.MediaAssetId,
                Shared = db.CaseMediaLinks.Any(other => other.MediaAssetId == l.MediaAssetId && other.CaseId != caseId),
            })
            .ToListAsync(cancellationToken);

        var personIds = db.Persons.Where(p => p.CaseId == caseId).Select(p => p.Id);

        return new CaseComposition(
            caseId,
            head.Number,
            head.Classification,
            head.DivisionId,
            ExclusiveAssetIds: [.. assets.Where(a => !a.Shared).Select(a => a.MediaAssetId).Distinct().Order()],
            SharedAssetIds: [.. assets.Where(a => a.Shared).Select(a => a.MediaAssetId).Distinct().Order()],
            Persons: await personIds.CountAsync(cancellationToken),
            ReferencePhotos: await db.ReferencePhotos.CountAsync(r => personIds.Contains(r.PersonId), cancellationToken),
            Appearances: await db.Appearances.CountAsync(a => a.CaseId == caseId, cancellationToken),
            Authorizations: await db.SearchAuthorizations.CountAsync(a => a.CaseId == caseId, cancellationToken),
            DocumentLinks: await db.CaseDocumentLinks.CountAsync(l => l.CaseId == caseId, cancellationToken));
    }

    /// <inheritdoc />
    public async Task<CaseWriteResult> PurgeAsync(int caseId, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);

        var role = await ResolveRoleAsync(access, cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var accessible = await CaseAccessRule.Apply(db.Cases, access, policy, role)
            .AnyAsync(c => c.Id == caseId, cancellationToken);
        if (!accessible)
        {
            return CaseWriteResult.NotFound;
        }

        // Одна транзакция: либо дело ушло целиком, либо не ушло ничего. Появления удаляются явно по делу —
        // внешний ключ у них на фигуранта, и каскад от дела их тоже снял бы, но только через фигурантов;
        // явное удаление не оставляет шанса «появлению» с фигурантом из другого дела. Остальное —
        // фигуранты (→ эталоны, появления), привязки носителей и документов, основания поиска, акт
        // закрытия — уходит каскадом внешних ключей от дела.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Appearances.Where(a => a.CaseId == caseId).ExecuteDeleteAsync(cancellationToken);
        await db.Cases.Where(c => c.Id == caseId).ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return CaseWriteResult.Ok;
    }

    /// <summary>
    /// Согласованы ли реквизиты задания с видом дела (ТФ-ДЕЛ-05) и справочниками (ТФ-АДМ-07): у задания —
    /// обязательные поля непусты, инициатор, звание и должность — существующие записи СВОЕГО вида; у иного
    /// вида реквизитов нет вовсе. Выключенная запись допустима только если она уже стоит в деле
    /// (<paramref name="current"/>): выключение записи не блокирует правку старых заданий, но новое
    /// значение берётся только из действующих.
    /// </summary>
    private static async Task<bool> IsTaskConsistentAsync(
        InvestigationDbContext db, CaseKind kind, TaskRequisites? task, CaseFile? current, CancellationToken cancellationToken)
    {
        if (kind != CaseKind.ObjectTask)
        {
            return task is null;
        }

        if (task is null
            || string.IsNullOrWhiteSpace(task.TaskNumber)
            || string.IsNullOrWhiteSpace(task.Justification)
            || string.IsNullOrWhiteSpace(task.Purpose))
        {
            return false;
        }

        var wanted = new List<(int Id, ReferenceKind Kind, int? Current)>
        {
            (task.InitiatorUnitId, ReferenceKind.InitiatorUnit, current?.InitiatorUnitId),
        };
        if (task.InitiatorRankId is { } rankId)
        {
            wanted.Add((rankId, ReferenceKind.Rank, current?.InitiatorRankId));
        }

        if (task.InitiatorPositionId is { } positionId)
        {
            wanted.Add((positionId, ReferenceKind.Position, current?.InitiatorPositionId));
        }

        var ids = wanted.Select(w => w.Id).Distinct().ToArray();
        var items = await db.ReferenceItems.AsNoTracking()
            .Where(i => ids.Contains(i.Id))
            .Select(i => new { i.Id, i.Kind, i.IsActive })
            .ToListAsync(cancellationToken);

        return wanted.All(w => items.Any(i => i.Id == w.Id && i.Kind == w.Kind && (i.IsActive || w.Current == w.Id)));
    }

    /// <summary>Переносит реквизиты задания в сущность целиком; <see langword="null"/> очищает все поля.</summary>
    private static void ApplyTaskRequisites(CaseFile entity, TaskRequisites? task)
    {
        entity.TaskNumber = Clean(task?.TaskNumber);
        entity.InitiatorUnitId = task?.InitiatorUnitId;
        entity.InitiatorName = Clean(task?.InitiatorName);
        entity.InitiatorRankId = task?.InitiatorRankId;
        entity.InitiatorPositionId = task?.InitiatorPositionId;
        entity.InitiatorPhone = Clean(task?.InitiatorPhone);
        entity.InitiatorDetails = Clean(task?.InitiatorDetails);
        entity.Justification = Clean(task?.Justification);
        entity.Purpose = Clean(task?.Purpose);
        entity.TaskNotes = Clean(task?.Notes);
    }

    /// <summary>Реквизиты задания дела; у иного вида — <see langword="null"/>.</summary>
    private static TaskRequisites? ToTaskRequisites(CaseFile entity) =>
        entity is { Kind: CaseKind.ObjectTask, TaskNumber: { } number, InitiatorUnitId: { } unitId }
            ? new TaskRequisites(
                number, unitId, entity.Justification ?? string.Empty, entity.Purpose ?? string.Empty,
                entity.InitiatorName, entity.InitiatorRankId, entity.InitiatorPositionId, entity.InitiatorPhone,
                entity.InitiatorDetails, entity.TaskNotes)
            : null;

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // Роль для правила видимости дел: без права «Дашборд и реестр дел» (матрица доступа, ADR-0032) — null, и
    // CaseAccessRule вернёт пусто (ТБ-012/021).
    private Task<InvestigationRole?> ResolveRoleAsync(AccessContext access, CancellationToken cancellationToken) =>
        PermissionRule.ResolveCaseViewerAsync(roles, access.NumericSubjectId, cancellationToken);
}
