using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Domain.Entities;
using ISC.AI.Profile.Investigation.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Profile.Investigation.Data.Exchange;

/// <summary>
/// Очередь «Конфликты обмена» (ТФ-ОБМ-02, ADR-0068 v0.3): конфликты приёма в пределах допуска сотрудника и решения
/// Руководителя по ним. Решение только записывается — применяет его следующий приём пакета (<see cref="ExchangeRecordImporter"/>,
/// <see cref="ExchangeImportService"/>).
/// </summary>
/// <remarks>
/// ИНВАРИАНТ ДОСТУПА (ТБ-020/021): и список, и решение идут через решётку гриф/подразделение (базовый фильтр ядра и
/// политика профиля) — конфликт вне допуска не виден и не решается, отказ неотличим от «нет такого». Своё дело для
/// объединения показывается словами только тому, кому оно доступно.
/// </remarks>
public sealed class ExchangeConflictStore(
    IDbContextFactory<InvestigationDbContext> contextFactory,
    IAccessPolicy policy,
    TimeProvider timeProvider) : IExchangeConflicts
{
    /// <summary>Предел комментария к решению.</summary>
    public const int CommentLimit = 1000;

    // История в очереди — последние.
    private const int HistoryLimit = 200;

    /// <inheritdoc />
    public async Task<IReadOnlyList<ExchangeConflictRow>> ListAsync(
        AccessContext access, bool includeHistory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var visible = db.ExchangeImportConflicts.AsNoTracking()
            .Where(BaselineAccess.Filter<ExchangeImportConflict>(access))
            .Where(policy.BuildFilter<ExchangeImportConflict>(access));
        var open = await visible
            .Where(c => c.Status == ExchangeConflictStatus.Pending || c.Status == ExchangeConflictStatus.Decided)
            .OrderBy(c => c.Status).ThenBy(c => c.Id)
            .ToListAsync(cancellationToken);
        var rows = open;
        if (includeHistory)
        {
            rows = [.. open, .. await visible
                .Where(c => c.Status == ExchangeConflictStatus.Applied || c.Status == ExchangeConflictStatus.Cleared)
                .OrderByDescending(c => c.UpdatedAt ?? c.CreatedAt).ThenByDescending(c => c.Id)
                .Take(HistoryLimit)
                .ToListAsync(cancellationToken)];
        }

        var importIds = rows.Select(r => r.ImportId).Distinct().ToList();
        var packages = await db.ExchangeImports.AsNoTracking().Where(i => importIds.Contains(i.Id))
            .ToDictionaryAsync(i => i.Id, i => i.PackageName, cancellationToken);
        var caseIds = rows.Where(r => r.Kind == ExchangeConflictKind.Case)
            .SelectMany(r => new[] { r.LocalId, r.DecisionLocalId })
            .Where(id => id is not null).Select(id => id!.Value).Distinct().ToList();
        var cases = await db.Cases.AsNoTracking()
            .Where(BaselineAccess.Filter<CaseFile>(access))
            .Where(policy.BuildFilter<CaseFile>(access))
            .Where(c => caseIds.Contains(c.Id))
            .Select(c => new { c.Id, c.Number, c.Title })
            .ToDictionaryAsync(c => c.Id, c => "дело № " + c.Number + " «" + c.Title + "»", cancellationToken);

        // Номер занят — по нынешнему состоянию, а не по тексту причины: после неудачного «создать новое» причина другая,
        // а номер по-прежнему занят — Руководителю нужно поле для нового номера.
        var numbered = rows.Where(r => r.Kind == ExchangeConflictKind.Case && r.Label is not null).ToList();
        var labels = numbered.Select(r => r.Label!).Distinct().ToList();
        var divisionIds = numbered.Select(r => r.DivisionId).Distinct().ToList();
        var taken = (await db.Cases.AsNoTracking()
                .Where(c => divisionIds.Contains(c.DivisionId) && labels.Contains(c.Number))
                .Select(c => new { c.DivisionId, c.Number })
                .ToListAsync(cancellationToken))
            .Select(c => (c.DivisionId, c.Number))
            .ToHashSet();

        return rows.Select(r =>
        {
            var local = r.DecisionLocalId ?? r.LocalId;
            return new ExchangeConflictRow(
                r.Id,
                r.Kind,
                r.SourceInstance,
                packages.GetValueOrDefault(r.ImportId),

                // Когда конфликт найден впервые: время решения и повторных приёмов здесь не подменяет его (живая проверка
                // 06.11.2026 — после решения столбец «Найден» показывал время решения).
                r.CreatedAt,
                r.Label,
                r.Reason,
                r.Details,
                local,
                r.Kind == ExchangeConflictKind.Case && local is { } id && cases.TryGetValue(id, out var label) ? label : null,
                r.Status,
                r.Decision,
                r.DecisionNumber,
                r.DecisionComment,
                r.DecidedByUserId,
                r.DecidedAt,
                r.AppliedAt,
                r.Kind == ExchangeConflictKind.Case && r.Label is { } number && taken.Contains((r.DivisionId, number)));
        }).ToList();
    }

    /// <inheritdoc />
    public async Task<ExchangeDecisionResult> DecideAsync(
        int conflictId,
        ExchangeConflictDecision decision,
        string? newNumber,
        string? comment,
        AccessContext access,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(access);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var conflict = await db.ExchangeImportConflicts
            .Where(BaselineAccess.Filter<ExchangeImportConflict>(access))
            .Where(policy.BuildFilter<ExchangeImportConflict>(access))
            .FirstOrDefaultAsync(c => c.Id == conflictId, cancellationToken);
        if (conflict is null)
        {
            return ExchangeDecisionResult.Missing;
        }

        if (conflict.Status is not (ExchangeConflictStatus.Pending or ExchangeConflictStatus.Decided))
        {
            return ExchangeDecisionResult.Refused("Решение уже применено или конфликт снят — менять его поздно.");
        }

        if (!Enum.IsDefined(decision))
        {
            return ExchangeDecisionResult.Refused("Неизвестное решение.");
        }

        if (decision == ExchangeConflictDecision.CreateNew && conflict.Kind != ExchangeConflictKind.Case)
        {
            return ExchangeDecisionResult.Refused("«Создать новое» — только для дела: одинаковый файл — один носитель (ТФ-МЕД-04).");
        }

        if (decision == ExchangeConflictDecision.Merge && conflict.LocalId is null)
        {
            return ExchangeDecisionResult.Refused("Своей записи для объединения нет.");
        }

        // Объединяют только с тем своим делом, которое Руководитель видит: решать по содержанию вслепую нельзя.
        if (decision == ExchangeConflictDecision.Merge && conflict.Kind == ExchangeConflictKind.Case
            && !await db.Cases.AsNoTracking()
                .Where(BaselineAccess.Filter<CaseFile>(access))
                .Where(policy.BuildFilter<CaseFile>(access))
                .AnyAsync(c => c.Id == conflict.LocalId, cancellationToken))
        {
            return ExchangeDecisionResult.Refused("Своё дело для объединения вне вашего допуска или уничтожено.");
        }

        string? number = null;
        var sourceNumber = conflict.Label?.Trim();
        if (decision == ExchangeConflictDecision.CreateNew
            && sourceNumber is { Length: > 0 }
            && await db.Cases.AnyAsync(c => c.DivisionId == conflict.DivisionId && c.Number == sourceNumber, cancellationToken))
        {
            // Совпал номер в подразделении: второе дело с тем же номером таблица не примет — нужен новый незанятый номер.
            number = newNumber?.Trim();
            if (string.IsNullOrEmpty(number))
            {
                return ExchangeDecisionResult.Refused("Номер совпал с делом подразделения — задайте номер нового дела.");
            }

            if (number.Length > 100)
            {
                return ExchangeDecisionResult.Refused("Номер дела длиннее 100 знаков.");
            }

            var taken = number;
            if (await db.Cases.AnyAsync(c => c.DivisionId == conflict.DivisionId && c.Number == taken, cancellationToken))
            {
                return ExchangeDecisionResult.Refused("Номер «" + number + "» в подразделении уже занят.");
            }
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var text = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        conflict.Decision = decision;
        conflict.DecisionLocalId = decision == ExchangeConflictDecision.Merge ? conflict.LocalId : null;
        conflict.DecisionNumber = number;
        conflict.DecisionComment = text is { Length: > CommentLimit } ? text[..CommentLimit] : text;
        conflict.DecidedByUserId = access.NumericSubjectId;
        conflict.DecidedAt = now;
        conflict.Status = ExchangeConflictStatus.Decided;
        conflict.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return ExchangeDecisionResult.Done;
    }
}
