using ISC.AI.Profile.Investigation.Domain.Entities;
using ISC.AI.Profile.Investigation.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Profile.Investigation.Data;

/// <summary>Учёт перенесённых записей баз Access (ADR-0043 п. 6) в <c>investigation.access_import_record</c>.</summary>
/// <remarks>
/// Решётки здесь нет намеренно: это служебная таблица соответствий для повторного переноса, наружу пользователю она не
/// выдаётся — дела и фигуранты по этим номерам читаются только через хранилища с решёткой.
/// </remarks>
public sealed class AccessImportRecordStore(IDbContextFactory<InvestigationDbContext> contextFactory) : IAccessImportRecordStore
{
    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<(string Table, string Key), AccessImportRecord>> ListAsync(
        string fileSha256, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileSha256);

        var sha = fileSha256.ToUpperInvariant();
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.AccessImportRecords.AsNoTracking()
            .Where(r => r.FileSha256 == sha)
            .Select(r => new AccessImportRecord(r.FileSha256, r.SourceTable, r.SourceKey, r.CaseId, r.PersonId))
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(r => (r.SourceTable, r.SourceKey));
    }

    /// <inheritdoc />
    public async Task SaveAsync(AccessImportRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        var sha = record.FileSha256.ToUpperInvariant();
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.AccessImportRecords.FirstOrDefaultAsync(
            r => r.FileSha256 == sha && r.SourceTable == record.SourceTable && r.SourceKey == record.SourceKey, cancellationToken);
        if (existing is null)
        {
            db.AccessImportRecords.Add(new AccessImportRecordEntity
            {
                FileSha256 = sha,
                SourceTable = record.SourceTable,
                SourceKey = record.SourceKey,
                CaseId = record.CaseId,
                PersonId = record.PersonId,
            });
        }
        else
        {
            existing.CaseId = record.CaseId;
            existing.PersonId = record.PersonId;
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
