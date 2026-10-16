using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Domain.Entities;
using ISC.AI.Profile.Investigation.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Profile.Investigation.Data;

/// <summary>Дела, по которым сотрудник может спрашивать в чате (ADR-0051/0052), — по полной решётке дел.</summary>
/// <remarks>
/// ИНВАРИАНТ (ТБ-020/021/071): дело выдаётся только при видимости по <see cref="CaseAccessRule.Apply"/>; без права
/// «Дашборд и реестр дел» не видно ни одного. Невидимое и несуществующее дело неразличимы.
/// </remarks>
public sealed class CaseChatScopes(
    IDbContextFactory<InvestigationDbContext> contextFactory,
    IAccessPolicy policy,
    IUserRoleStore roles) : ICaseChatScopes
{
    /// <inheritdoc />
    public async Task<CaseChatScope?> GetScopeAsync(int caseId, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await VisibleCases(db, access, cancellationToken) is { } cases
            ? await cases.Where(c => c.Id == caseId)
                .Select(c => new CaseChatScope(c.Id, c.Number, c.Classification, c.DivisionId, c.Title))
                .FirstOrDefaultAsync(cancellationToken)
            : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CaseChatScope>> ListScopesAsync(AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await VisibleCases(db, access, cancellationToken) is { } cases
            ? await cases
                .OrderByDescending(c => c.UpdatedAt ?? c.CreatedAt).ThenByDescending(c => c.Id)
                .Select(c => new CaseChatScope(c.Id, c.Number, c.Classification, c.DivisionId, c.Title))
                .ToListAsync(cancellationToken)
            : [];
    }

    // Дела, видимые субъекту, по полной решётке; без права «Дашборд и реестр дел» — null (ничего не видно).
    private async Task<IQueryable<CaseFile>?> VisibleCases(InvestigationDbContext db, AccessContext access, CancellationToken cancellationToken)
    {
        var role = await PermissionRule.ResolveCaseViewerAsync(roles, access.NumericSubjectId, cancellationToken);
        return role is null ? null : CaseAccessRule.Apply(db.Cases.AsNoTracking(), access, policy, role);
    }
}
