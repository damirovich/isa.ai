using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Profile.Inspector.Domain.Entities;
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
        DateOnly from, DateOnly to, DashboardFilter? filter = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var period = ApplyFilter(
            db.Violations.AsNoTracking().Where(v => v.DetectedAt >= from && v.DetectedAt <= to),
            filter, includeRemediationStatus: true);

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

        // Предыдущий период равной длины — для тренда, по подразделениям. Отбор применяется
        // В ТЕХ ЖЕ рамках (кроме статуса устранения — он про текущее состояние, не про период):
        // иначе тренд сравнивал бы отфильтрованное с полным и всегда «падал бы».
        var days = to.DayNumber - from.DayNumber;
        var previousFrom = from.AddDays(-(days + 1));
        var previousTo = from.AddDays(-1);
        var previousCounts = await ApplyFilter(
                db.Violations.AsNoTracking()
                    .Where(v => v.DetectedAt >= previousFrom && v.DetectedAt <= previousTo),
                filter, includeRemediationStatus: false)
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

    /// <summary>
    /// Отбор дашборда (ТФ-ДШ-02) поверх выборки нарушений. Сфера классификатора включает
    /// её виды — тот же приём, что в архиве проверок (ArchiveFilter.CategoryId).
    /// </summary>
    private static IQueryable<Violation> ApplyFilter(
        IQueryable<Violation> query, DashboardFilter? filter, bool includeRemediationStatus)
    {
        if (filter is null)
        {
            return query;
        }

        if (filter.DivisionId is { } divisionId)
        {
            query = query.Where(v => v.DivisionId == divisionId);
        }

        if (filter.CategoryId is { } categoryId)
        {
            query = query.Where(v => v.CategoryId == categoryId || v.Category!.ParentId == categoryId);
        }

        if (filter.Severity is { } severity)
        {
            query = query.Where(v => v.Severity == severity);
        }

        if (includeRemediationStatus && filter.RemediationStatus is { } status)
        {
            query = query.Where(v => v.RemediationStatus == status);
        }

        return query;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<DivisionRiskDetail>> GetDivisionRisksAsync(
        DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Сигналы — те же определения, что у дашборда (иначе экраны разошлись бы в цифрах).
        // «Сфера» болевой точки — родитель вида; нарушение, отнесённое к сфере напрямую, — она сама.
        var rows = await db.Violations.AsNoTracking()
            .Where(v => v.DetectedAt >= from && v.DetectedAt <= to)
            .Select(v => new
            {
                v.DivisionId,
                DivisionName = v.Division!.Name,
                v.Severity,
                v.RemediationStatus,
                v.DetectedAt,
                v.Id,
                v.Recommendation,
                SphereName = v.Category!.Parent != null ? v.Category.Parent.Name : v.Category.Name,
                IsRepeat = db.Violations.Any(o => o.DivisionId == v.DivisionId
                    && o.CategoryId == v.CategoryId
                    && (o.DetectedAt < v.DetectedAt || (o.DetectedAt == v.DetectedAt && o.Id < v.Id))),
            })
            .ToListAsync(cancellationToken);

        var days = to.DayNumber - from.DayNumber;
        var previousCounts = await db.Violations.AsNoTracking()
            .Where(v => v.DetectedAt >= from.AddDays(-(days + 1)) && v.DetectedAt <= from.AddDays(-1))
            .GroupBy(v => v.DivisionId)
            .Select(g => new { DivisionId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.DivisionId, g => g.Count, cancellationToken);

        return [.. rows
            .GroupBy(r => new { r.DivisionId, r.DivisionName })
            .Select(g =>
            {
                var open = g.Where(r => r.RemediationStatus != RemediationStatus.Resolved).ToList();
                var signals = new RiskSignals(
                    OpenSeverities: [.. open.Select(r => r.Severity)],
                    RepeatCount: g.Count(r => r.IsRepeat),
                    OverdueCount: g.Count(r => r.RemediationStatus == RemediationStatus.Overdue),
                    TrendDelta: g.Count() - previousCounts.GetValueOrDefault(g.Key.DivisionId));
                return new DivisionRiskDetail(
                    g.Key.DivisionId,
                    g.Key.DivisionName,
                    g.Count(),
                    open.Count,
                    signals.RepeatCount,
                    signals.OverdueCount,
                    signals.TrendDelta,
                    TopSpheres: [.. g.GroupBy(r => r.SphereName)
                        .OrderByDescending(s => s.Count()).ThenBy(s => s.Key)
                        .Take(3)
                        .Select(s => $"{s.Key} — {s.Count()}")],
                    LastRecommendation: g.Where(r => !string.IsNullOrWhiteSpace(r.Recommendation))
                        .OrderByDescending(r => r.DetectedAt).ThenByDescending(r => r.Id)
                        .Select(r => r.Recommendation)
                        .FirstOrDefault(),
                    Assessment: calculator.Assess(signals));
            })
            .OrderByDescending(d => d.Assessment.Score).ThenBy(d => d.DivisionName)];
    }

    /// <inheritdoc />
    public async Task<RemediationSummary> GetRemediationAsync(
        DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var period = db.Violations.AsNoTracking()
            .Where(v => v.DetectedAt >= from && v.DetectedAt <= to);

        // Карточки: неустранённые важнее устранённых, внутри группы — свежие первыми.
        // Срез 100 — защита экрана от многотысячного периода (карточки листаются реестром, не здесь).
        var rows = await period
            .OrderBy(v => v.RemediationStatus == RemediationStatus.Resolved ? 1 : 0)
            .ThenByDescending(v => v.DetectedAt).ThenByDescending(v => v.Id)
            .Take(100)
            .Select(v => new
            {
                v.Id,
                DivisionName = v.Division!.Name,
                CategoryName = v.Category!.Name,
                v.Severity,
                v.DetectedAt,
                v.RemediationStatus,
                v.Recommendation,
                v.RemediationDeadline,
            })
            .ToListAsync(cancellationToken);

        // Проекция в анонимный тип (record в EF-проекции переводится не всегда — общий обход проекта).
        var divisions = await period
            .GroupBy(v => new { v.DivisionId, Name = v.Division!.Name })
            .Select(g => new
            {
                g.Key.DivisionId,
                g.Key.Name,
                Total = g.Count(),
                Resolved = g.Sum(v => v.RemediationStatus == RemediationStatus.Resolved ? 1 : 0),
                Partial = g.Sum(v => v.RemediationStatus == RemediationStatus.Partial ? 1 : 0),
                UnderControl = g.Sum(v => v.RemediationStatus == RemediationStatus.UnderControl ? 1 : 0),
                Overdue = g.Sum(v => v.RemediationStatus == RemediationStatus.Overdue ? 1 : 0),
            })
            .ToListAsync(cancellationToken);

        return new RemediationSummary(
            [.. rows.Select(r => new RemediationRow(
                r.Id, r.DivisionName, r.CategoryName, r.Severity, r.DetectedAt,
                r.RemediationStatus, r.Recommendation, r.RemediationDeadline))],
            [.. divisions
                .OrderBy(d => d.Name)
                .Select(d => new DivisionRemediationRow(
                    d.DivisionId, d.Name, d.Total, d.Resolved, d.Partial, d.UnderControl, d.Overdue))]);
    }
}
