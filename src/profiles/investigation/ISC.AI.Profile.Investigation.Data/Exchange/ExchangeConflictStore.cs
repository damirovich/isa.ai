using System.Globalization;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Domain.Entities;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Profile.Investigation.Data.Exchange;

/// <summary>
/// Очередь «Конфликты обмена» (ТФ-ОБМ-02, ADR-0068 v0.3–v0.4): конфликты приёма в пределах допуска сотрудника и решения
/// Руководителя по ним. Решение по делу и носителю только записывается — применяет его следующий приём пакета
/// (<see cref="ExchangeRecordImporter"/>, <see cref="ExchangeImportService"/>); решение по конфликту изменений принятой
/// записи применяется сразу (<see cref="ExchangeRecordUpdater.ApplyDecisionAsync"/>).
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

        // Конфликты изменений (v0.4): своя запись реквизита открывается карточкой объекта, участника — графом события.
        var owners = await OwnersAsync(db, rows, cancellationToken);

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
                r.Kind == ExchangeConflictKind.Case && r.Label is { } number && taken.Contains((r.DivisionId, number)),
                r.RecordKind,
                r.Kind == ExchangeConflictKind.RecordUpdate ? ExchangeRecordUpdater.ParseChanges(r.Changes) : null,
                r.LocalId is { } own && owners.TryGetValue((r.RecordKind, own), out var owner) ? owner : null);
        }).ToList();
    }

    private static async Task<Dictionary<(ExchangeRecordKind, int), int>> OwnersAsync(
        InvestigationDbContext db, List<ExchangeImportConflict> rows, CancellationToken cancellationToken)
    {
        var result = new Dictionary<(ExchangeRecordKind, int), int>();
        List<int> Ids(ExchangeRecordKind kind) => rows
            .Where(r => r.Kind == ExchangeConflictKind.RecordUpdate && r.RecordKind == kind && r.LocalId is not null)
            .Select(r => r.LocalId!.Value).Distinct().ToList();

        var addresses = Ids(ExchangeRecordKind.Address);
        foreach (var a in await db.PersonAddresses.AsNoTracking().Where(a => addresses.Contains(a.Id)).Select(a => new { a.Id, a.PersonId }).ToListAsync(cancellationToken))
        {
            result[(ExchangeRecordKind.Address, a.Id)] = a.PersonId;
        }

        var phones = Ids(ExchangeRecordKind.Phone);
        foreach (var p in await db.PersonPhones.AsNoTracking().Where(p => phones.Contains(p.Id)).Select(p => new { p.Id, p.PersonId }).ToListAsync(cancellationToken))
        {
            result[(ExchangeRecordKind.Phone, p.Id)] = p.PersonId;
        }

        var vehicles = Ids(ExchangeRecordKind.Vehicle);
        foreach (var v in await db.PersonVehicles.AsNoTracking().Where(v => vehicles.Contains(v.Id)).Select(v => new { v.Id, v.PersonId }).ToListAsync(cancellationToken))
        {
            result[(ExchangeRecordKind.Vehicle, v.Id)] = v.PersonId;
        }

        var participants = Ids(ExchangeRecordKind.Participant);
        foreach (var p in await db.LinkEventParticipants.AsNoTracking().Where(p => participants.Contains(p.Id)).Select(p => new { p.Id, p.EventId }).ToListAsync(cancellationToken))
        {
            result[(ExchangeRecordKind.Participant, p.Id)] = p.EventId;
        }

        return result;
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

        if (conflict.Kind == ExchangeConflictKind.RecordUpdate)
        {
            return await DecideUpdateAsync(db, conflict, decision, comment, access, cancellationToken);
        }

        if (decision is ExchangeConflictDecision.AcceptSource or ExchangeConflictDecision.KeepLocal)
        {
            return ExchangeDecisionResult.Refused("«Принять версию источника» и «оставить свою» — только для конфликта изменений принятой записи.");
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

    // Конфликт изменений (ADR-0068 v0.4): решение применяется СРАЗУ — значения источника уже в строке конфликта, пакет и
    // ключ хранителя не нужны. Запись и строка конфликта сохраняются вместе: либо применено и отмечено, либо ничего.
    // ИНВАРИАНТ: значения пишутся только в запись, доступную решающему, — гриф и подразделение строки конфликта — её
    // (ТБ-020/021), а строку вне допуска сюда не пропускает фильтр выше.
    private async Task<ExchangeDecisionResult> DecideUpdateAsync(
        InvestigationDbContext db,
        ExchangeImportConflict conflict,
        ExchangeConflictDecision decision,
        string? comment,
        AccessContext access,
        CancellationToken cancellationToken)
    {
        if (decision is not (ExchangeConflictDecision.AcceptSource or ExchangeConflictDecision.KeepLocal))
        {
            return ExchangeDecisionResult.Refused("По конфликту изменений решения — «принять версию источника» или «оставить свою».");
        }

        if (conflict.Status != ExchangeConflictStatus.Pending)
        {
            return ExchangeDecisionResult.Refused("Решение уже применено или конфликт снят — менять его поздно.");
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var text = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        conflict.Decision = decision;
        conflict.DecisionComment = text is { Length: > CommentLimit } ? text[..CommentLimit] : text;
        conflict.DecidedByUserId = access.NumericSubjectId;
        conflict.DecidedAt = now;
        conflict.Status = ExchangeConflictStatus.Applied;
        conflict.AppliedAt = now;
        conflict.UpdatedAt = now;
        var caseId = conflict.RecordKind == ExchangeRecordKind.Case ? conflict.LocalId : null;
        var wasClosed = caseId is { } before && await IsClosedAsync(db, before, cancellationToken);
        var problem = await ExchangeRecordUpdater.ApplyDecisionAsync(
            db,
            conflict,
            decision == ExchangeConflictDecision.AcceptSource,
            "версия " + conflict.SourceInstance + " принята по решению Руководителя (конфликт обмена № " + conflict.Id.ToString(CultureInfo.InvariantCulture) + ")",
            access.NumericSubjectId,
            cancellationToken);
        if (problem is not null)
        {
            return ExchangeDecisionResult.Refused(problem);
        }

        // Принятый статус источника закрыл своё дело — регламент закрытия (ТБ-074) исполнит сценарий решения.
        return caseId is { } after && !wasClosed && await IsClosedAsync(db, after, cancellationToken)
            ? ExchangeDecisionResult.Applied with { ClosedCaseId = after }
            : ExchangeDecisionResult.Applied;
    }

    private static Task<bool> IsClosedAsync(InvestigationDbContext db, int caseId, CancellationToken cancellationToken) =>
        db.Cases.AsNoTracking().AnyAsync(c => c.Id == caseId && c.Status == CaseStatus.Closed, cancellationToken);
}
