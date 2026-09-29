using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Domain.Entities;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Profile.Investigation.Data;

/// <summary>
/// Документы дела поверх <c>investigation.case_document_link</c> (ТФ-ДЕЛ-02). Каждая операция начинается с
/// видимости дела по полной решётке (<see cref="CaseAccessRule.Apply"/>): недоступное дело неотличимо от
/// отсутствующего (ТБ-021). Уничтожение дела (ADR-0025) снимает привязки каскадом, документы остаются.
/// </summary>
public sealed class CaseDocumentStore(
    IDbContextFactory<InvestigationDbContext> contextFactory,
    IAccessPolicy policy,
    IUserRoleStore roles) : ICaseDocumentStore
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<CaseDocumentLinkRow>?> ListAsync(
        int caseId, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await CaseVisibleAsync(db, caseId, access, cancellationToken))
        {
            return null;
        }

        var rows = await db.CaseDocumentLinks.AsNoTracking()
            .Where(l => l.CaseId == caseId)
            .OrderByDescending(l => l.CreatedAt).ThenByDescending(l => l.Id)
            .Select(l => new { l.DocFlowDocumentId, l.LinkedByUserId, l.CreatedAt })
            .ToListAsync(cancellationToken);
        return rows
            .Select(r => new CaseDocumentLinkRow(r.DocFlowDocumentId, r.LinkedByUserId, DateTime.SpecifyKind(r.CreatedAt, DateTimeKind.Utc)))
            .ToList();
    }

    /// <inheritdoc />
    public async Task<CaseDocumentWriteResult> AttachAsync(
        int caseId, int documentId, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await CaseVisibleAsync(db, caseId, access, cancellationToken))
        {
            return CaseDocumentWriteResult.NotFound;
        }

        if (await db.CaseDocumentLinks.AnyAsync(l => l.CaseId == caseId && l.DocFlowDocumentId == documentId, cancellationToken))
        {
            return CaseDocumentWriteResult.AlreadyLinked;
        }

        db.CaseDocumentLinks.Add(new CaseDocumentLink
        {
            CaseId = caseId,
            DocFlowDocumentId = documentId,
            LinkedByUserId = access.NumericSubjectId,
            CreatedAt = DateTime.UtcNow,
        });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            // Два одновременных прикрепления: уникальный индекс (case_id, document_id) — последний рубеж.
            return CaseDocumentWriteResult.AlreadyLinked;
        }

        return CaseDocumentWriteResult.Ok;
    }

    /// <inheritdoc />
    public async Task<CaseDocumentWriteResult> DetachAsync(
        int caseId, int documentId, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await CaseVisibleAsync(db, caseId, access, cancellationToken))
        {
            return CaseDocumentWriteResult.NotFound;
        }

        var removed = await db.CaseDocumentLinks
            .Where(l => l.CaseId == caseId && l.DocFlowDocumentId == documentId)
            .ExecuteDeleteAsync(cancellationToken);
        return removed > 0 ? CaseDocumentWriteResult.Ok : CaseDocumentWriteResult.NotLinked;
    }

    private async Task<bool> CaseVisibleAsync(InvestigationDbContext db, int caseId, AccessContext access, CancellationToken cancellationToken)
    {
        var role = access.NumericSubjectId is { } userId ? await roles.GetRoleAsync(userId, cancellationToken) : (InvestigationRole?)null;
        return await CaseAccessRule.Apply(db.Cases.AsNoTracking(), access, policy, role).AnyAsync(c => c.Id == caseId, cancellationToken);
    }
}
