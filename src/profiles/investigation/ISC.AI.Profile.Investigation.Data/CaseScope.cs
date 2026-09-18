using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Domain.Services;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Profile.Investigation.Data;

/// <summary>
/// Реализация порта пакета «Медиа» <see cref="ICaseScope"/>: область дел субъекта (ТБ-071, ТФ-ПЛ-05)
/// и связь носителей/фигурантов с делами. Списки дел, основания и фигуранты — тонкая обёртка над
/// <see cref="ICaseStore"/> и <see cref="IPersonStore"/> (решётка живёт там); проверки носителя по
/// прямому идентификатору (<see cref="IsAssetAccessibleAsync"/>, <see cref="GetCaseIdForAssetAsync"/>)
/// выполняются здесь одним SQL-запросом: привязки <c>case_media_link</c> ∩ дела, прошедшие
/// <see cref="CaseAccessRule.Apply"/> (floor ядра → политика профиля → роль/владение).
/// </summary>
/// <remarks>
/// Роль субъекта читается через <see cref="IUserRoleStore"/> по <see cref="AccessContext.NumericSubjectId"/>
/// на каждую операцию — не кэшируется (ТБ-016). Без субъекта или без роли <see cref="CaseAccessRule.Narrow"/>
/// даёт пустое множество дел — значит, ни один носитель не доступен (default-deny, ТБ-012).
/// </remarks>
public sealed class CaseScope(
    ICaseStore cases,
    IPersonStore persons,
    IDbContextFactory<InvestigationDbContext> contextFactory,
    IAccessPolicy policy,
    IUserRoleStore roles,
    InvestigationRetentionOptions retention) : ICaseScope
{
    // Область «все доступные дела» для выбора: страница заведомо больше любого реального числа дел
    // одного субъекта; постранично модулю не нужно — он строит список выбора целиком.
    private const int AllCasesPageSize = 10_000;

    /// <inheritdoc />
    public async Task<IReadOnlyList<CaseScopeItem>> ListAccessibleCasesAsync(AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);

        var page = await cases.ListAsync(new CaseFilter(PageSize: AllCasesPageSize), access, cancellationToken);
        return page.Rows
            .Select(r => new CaseScopeItem(
                r.Id, r.Number, r.Title, r.Classification, r.DivisionId, r.Status == CaseStatus.Closed))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<CaseScopeItem?> GetCaseAsync(int caseId, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);

        // Fail-closed: недоступное и несуществующее дело — одинаково null (ТБ-020/021).
        var details = await cases.GetAsync(caseId, access, cancellationToken);
        return details is null
            ? null
            : new CaseScopeItem(
                details.Id, details.Number, details.Title, details.Classification, details.DivisionId,
                details.Status == CaseStatus.Closed);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CaseAuthorizationItem>> ListAuthorizationsAsync(int caseId, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);

        var details = await cases.GetAsync(caseId, access, cancellationToken);
        return details is null
            ? []
            : details.Authorizations.Select(a => new CaseAuthorizationItem(a.Id, a.Reference)).ToList();
    }

    /// <inheritdoc />
    public Task<IReadOnlyCollection<int>> GetAssetIdsAsync(IReadOnlyCollection<int> caseIds, CancellationToken cancellationToken = default) =>
        cases.ListMediaAssetIdsAsync(caseIds, cancellationToken);

    /// <inheritdoc />
    /// <remarks>
    /// ТБ-020/021: среди привязок носителя берётся первая (по порядку привязки), чьё дело доступно субъекту;
    /// недоступные дела наружу не раскрываются — карточка носителя не покажет ссылку на чужое дело
    /// (носитель после дедупликации по хешу может быть привязан к нескольким делам).
    /// </remarks>
    public async Task<int?> GetCaseIdForAssetAsync(int assetId, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);

        var role = await ResolveRoleAsync(access, cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var accessible = CaseAccessRule.Apply(db.Cases, access, policy, role);
        return await db.CaseMediaLinks.AsNoTracking()
            .Where(l => l.MediaAssetId == assetId && accessible.Any(c => c.Id == l.CaseId))
            .OrderBy(l => l.Id)
            .Select(l => (int?)l.CaseId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// ИНВАРИАНТ (ТБ-071, ТФ-ДЕЛ-03, ТБ-012): носитель доступен ⇔ существует привязка <c>case_media_link</c>
    /// к делу, проходящему полную решётку <see cref="CaseAccessRule.Apply"/>. Следователь того же подразделения
    /// и с тем же допуском чужой носитель НЕ видит; субъект без роли — ничего. Проверка выполняется на стороне
    /// БД одним <c>EXISTS</c>, отдельного «мягкого» пути нет.
    /// </remarks>
    public async Task<bool> IsAssetAccessibleAsync(int assetId, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);

        var role = await ResolveRoleAsync(access, cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var accessible = CaseAccessRule.Apply(db.Cases, access, policy, role);
        return await db.CaseMediaLinks.AsNoTracking()
            .AnyAsync(l => l.MediaAssetId == assetId && accessible.Any(c => c.Id == l.CaseId), cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// БЕЗ решётки — и это правильно: вопрос задаёт фоновый конвейер от имени системы, ответ пользователю
    /// не показывается. Носитель без привязок индексируется (запрета нет); привязанный — пока ОТКРЫТО хотя
    /// бы одно из его дел: пока есть действующее основание, шаблоны правомерны (ТБ-074, ADR-0024).
    /// </remarks>
    public async Task<bool> IsBiometricIndexingAllowedAsync(int assetId, CancellationToken cancellationToken = default)
    {
        // Запрет имеет смысл ТОЛЬКО там, где регламент действительно удаляет шаблоны по закрытию: иначе
        // запрещать нечего — шаблоны закрытого дела и так на месте, и переиндексация ничего не возвращает
        // из небытия (ADR-0024, решение эксплуатанта в ведомственном акте).
        if (!retention.PurgeTemplatesOnCaseClosure)
        {
            return true;
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var statuses = await db.CaseMediaLinks.AsNoTracking()
            .Where(l => l.MediaAssetId == assetId)
            .Join(db.Cases.AsNoTracking(), l => l.CaseId, c => c.Id, (_, c) => c.Status)
            .ToListAsync(cancellationToken);

        return statuses.Count == 0 || statuses.Exists(status => status != CaseStatus.Closed);
    }

    /// <inheritdoc />
    public async Task LinkAssetAsync(int caseId, int assetId, string? place, int? linkedByUserId, CancellationToken cancellationToken = default)
    {
        var result = await cases.LinkMediaAsync(caseId, assetId, place, linkedByUserId, cancellationToken);
        if (result != CaseWriteResult.Ok)
        {
            throw new InvalidOperationException($"Привязать носитель {assetId} к делу {caseId} не удалось: {result}.");
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CasePersonItem>> ListPersonsAsync(int caseId, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);

        var rows = await persons.ListByCaseAsync(caseId, access, cancellationToken);
        return rows.Select(p => new CasePersonItem(p.Id, p.DisplayName)).ToList();
    }

    /// <inheritdoc />
    public Task RecordAppearanceAsync(ConfirmedAppearance appearance, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(appearance);

        // ТБ-073: два «подтверждён» РАЗНЫХ сотрудников. Само правило — TwoPersonRule модуля; здесь
        // страховка на границе: самоподтверждение в «появление» не превращается ни при каких условиях.
        if (appearance.ExpertUserId == appearance.VerifierUserId)
        {
            throw new InvalidOperationException(
                "Одно лицо не может быть экспертом и верификатором одного результата (ТБ-073).");
        }

        var draft = new AppearanceDraft(
            appearance.PersonRef,
            appearance.CaseId,
            appearance.AssetId,
            appearance.FaceId,
            appearance.FrameIndex,
            appearance.FrameTimestampMs,
            appearance.SessionId,
            appearance.CandidateId,
            appearance.Similarity,
            appearance.ConfirmedAtUtc,
            appearance.ExpertUserId,
            appearance.VerifierUserId,
            appearance.Classification,
            appearance.DivisionId);

        return persons.AddAppearanceAsync(draft, cancellationToken);
    }

    private async Task<InvestigationRole?> ResolveRoleAsync(AccessContext access, CancellationToken cancellationToken) =>
        access.NumericSubjectId is { } userId
            ? await roles.GetRoleAsync(userId, cancellationToken)
            : null;
}
