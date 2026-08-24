using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Profile.Inspector.Domain.Enums;
using ISC.AI.Profile.Inspector.Domain.Risk;
using ISC.AI.Profile.Inspector.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Profile.Inspector.Data;

/// <summary>
/// Источник сигналов риска и сводки дашборда (<see cref="IRiskDataSource"/>, Э5-01 шаг 2) над
/// схемой <c>inspector</c>. Все агрегаты ДЕТЕРМИНИРОВАНЫ (Приложение §2): одни данные — один балл;
/// балл считает <see cref="RiskScoreCalculator"/>, не ИИ.
/// </summary>
/// <remarks>
/// Определения сигналов (за период [from, to]):
/// <list type="bullet">
/// <item>ОТКРЫТЫЕ тяжести — нарушения периода со статусом, отличным от «устранено» (w1);</item>
/// <item>ПОВТОРЫ — нарушения периода, у которых есть более раннее нарушение того же вида в том же
/// подразделении (в любое время до него) — «тот же вид в том же подразделении» (w2);</item>
/// <item>ПРОСРОЧКИ — нарушения периода в статусе «просрочено» (w3);</item>
/// <item>ТРЕНД — число нарушений периода минус число за предыдущий период равной длины (w4,
/// может быть отрицательным — снижение уменьшает балл).</item>
/// </list>
/// </remarks>
public sealed class RiskDataSource(
    IDbContextFactory<InspectorDbContext> contextFactory,
    RiskScoreCalculator calculator) : IRiskDataSource
{
    private const int RecentCount = 8;

    /// <inheritdoc />
    public async Task<DashboardSummary> GetDashboardAsync(
        DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var period = db.Violations.AsNoTracking()
            .Where(v => v.DetectedAt >= from && v.DetectedAt <= to);

        // Счётчики периода — одной поездкой (условный Count не переводится — Sum(cond ? 1 : 0)).
        var totals = await period
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Total = g.Count(),
                Critical = g.Sum(v => v.Severity == ViolationSeverity.Critical ? 1 : 0),
                Remediated = g.Sum(v => v.RemediationStatus == RemediationStatus.Resolved ? 1 : 0),
            })
            .FirstOrDefaultAsync(cancellationToken);

        // Сигналы по подразделениям. Повторность: есть более раннее нарушение того же вида в том же
        // подразделении (строго раньше по дате, при равенстве — по id: детерминированный порядок).
        var rows = await period
            .Select(v => new
            {
                v.DivisionId,
                DivisionName = v.Division!.Name,
                v.Severity,
                v.RemediationStatus,
                IsRepeat = db.Violations.Any(o => o.DivisionId == v.DivisionId
                    && o.CategoryId == v.CategoryId
                    && (o.DetectedAt < v.DetectedAt || (o.DetectedAt == v.DetectedAt && o.Id < v.Id))),
            })
            .ToListAsync(cancellationToken);

        // Предыдущий период равной длины — для тренда, по подразделениям.
        var days = to.DayNumber - from.DayNumber;
        var previousFrom = from.AddDays(-(days + 1));
        var previousTo = from.AddDays(-1);
        var previousCounts = await db.Violations.AsNoTracking()
            .Where(v => v.DetectedAt >= previousFrom && v.DetectedAt <= previousTo)
            .GroupBy(v => v.DivisionId)
            .Select(g => new { DivisionId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.DivisionId, g => g.Count, cancellationToken);

        var divisionRisks = rows
            .GroupBy(r => new { r.DivisionId, r.DivisionName })
            .Select(g =>
            {
                var signals = new RiskSignals(
                    OpenSeverities: [.. g.Where(r => r.RemediationStatus != RemediationStatus.Resolved).Select(r => r.Severity)],
                    RepeatCount: g.Count(r => r.IsRepeat),
                    OverdueCount: g.Count(r => r.RemediationStatus == RemediationStatus.Overdue),
                    TrendDelta: g.Count() - previousCounts.GetValueOrDefault(g.Key.DivisionId));
                return new DivisionRiskRow(
                    g.Key.DivisionId, g.Key.DivisionName, g.Count(), calculator.Assess(signals));
            })
            .OrderByDescending(r => r.Assessment.Score).ThenBy(r => r.DivisionName)
            .ToList();

        var recentRows = await period
            .OrderByDescending(v => v.DetectedAt).ThenByDescending(v => v.Id)
            .Take(RecentCount)
            .Select(v => new
            {
                v.Id,
                DivisionName = v.Division!.Name,
                CategoryName = v.Category!.Name,
                v.Severity,
                v.DetectedAt,
                v.RemediationStatus,
            })
            .ToListAsync(cancellationToken);

        return new DashboardSummary(
            totals?.Total ?? 0,
            totals?.Critical ?? 0,
            (totals?.Total ?? 0) - (totals?.Remediated ?? 0),
            totals?.Remediated ?? 0,
            divisionRisks,
            [.. recentRows.Select(r => new RecentViolationRow(
                r.Id, r.DivisionName, r.CategoryName, r.Severity, r.DetectedAt, r.RemediationStatus))]);
    }
}
