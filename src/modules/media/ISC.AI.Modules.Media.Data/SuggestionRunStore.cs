using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Data.Entities;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Modules.Media.Data;

/// <summary>
/// Журнал сверок носителей с эталонами фигурантов в схеме <c>media</c> (ТФ-ПЕР-09, ADR-0035). Чтение — под
/// решёткой ядра на стороне БД (ТБ-020/021) и только по делам из области субъекта (ТБ-071).
/// </summary>
public sealed class SuggestionRunStore(
    IDbContextFactory<MediaDbContext> contextFactory,
    IAccessPolicy accessPolicy) : ISuggestionRunStore
{
    /// <inheritdoc />
    public async Task RecordAsync(SuggestionRunDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        db.SuggestionRuns.Add(new SuggestionRun
        {
            AssetId = draft.AssetId,
            CaseId = draft.CaseId,
            Trigger = draft.Trigger,
            ReferencesChecked = draft.ReferencesChecked,
            SessionsCreated = draft.SessionsCreated,
            CandidatesCreated = draft.CandidatesCreated,
            MaxCosineDistance = draft.MaxCosineDistance,
            Classification = draft.Classification,
            DivisionId = draft.DivisionId,
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SuggestionRunRow>> ListLatestAsync(
        int assetId, IReadOnlyCollection<int> caseIds, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caseIds);
        if (caseIds.Count == 0)
        {
            return []; // область дел пуста — читать нечего (ТБ-071)
        }

        var ids = caseIds as int[] ?? caseIds.ToArray();
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.SuggestionRuns.AsNoTracking()
            .VisibleTo(access, accessPolicy)
            .Where(r => r.AssetId == assetId && ids.Contains(r.CaseId))
            .Select(r => new SuggestionRunRow(
                r.Id, r.AssetId, r.CaseId, r.Trigger, r.ReferencesChecked, r.SessionsCreated, r.CandidatesCreated,
                r.MaxCosineDistance, r.CreatedAt))
            .ToListAsync(cancellationToken);

        // Сверок по носителю единицы — последняя по делу выбирается в памяти, без оконных функций.
        return rows
            .GroupBy(r => r.CaseId)
            .Select(g => g.OrderByDescending(r => r.CreatedAtUtc).ThenByDescending(r => r.Id).First())
            .OrderBy(r => r.CaseId)
            .Select(r => r with { CreatedAtUtc = DateTime.SpecifyKind(r.CreatedAtUtc, DateTimeKind.Utc) })
            .ToList();
    }
}
