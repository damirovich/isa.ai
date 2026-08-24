using ISC.AI.Profile.Inspector.Domain.Risk;

namespace ISC.AI.Profile.Inspector.Domain.Services;

/// <summary>Сводка дашборда (§5.2.5): счётчики за период + светофор риска по подразделениям.</summary>
public sealed record DashboardSummary(
    int TotalViolations,
    int CriticalViolations,
    int NotRemediated,
    int Remediated,
    IReadOnlyList<DivisionRiskRow> DivisionRisks,
    IReadOnlyList<RecentViolationRow> RecentViolations);

/// <summary>Строка светофора: подразделение + детерминированная оценка риска (Приложение §2).</summary>
public sealed record DivisionRiskRow(
    int DivisionId,
    string DivisionName,
    int ViolationCount,
    RiskAssessment Assessment);

/// <summary>Свежая запись для ленты дашборда.</summary>
public sealed record RecentViolationRow(
    int Id,
    string DivisionName,
    string CategoryName,
    Enums.ViolationSeverity Severity,
    DateOnly DetectedAt,
    Enums.RemediationStatus RemediationStatus);

/// <summary>
/// Источник сигналов риска и сводки дашборда (Э5-01 шаг 2): ДЕТЕРМИНИРОВАННЫЕ агрегаты из
/// <c>inspector.violation</c> — открытые тяжести, повторы (тот же вид в том же подразделении),
/// просроченные устранения, тренд к предыдущему периоду равной длины. Балл считает
/// <see cref="RiskScoreCalculator"/> — код, не ИИ (воспроизводимость приёмки, §5.2.5.1).
/// </summary>
public interface IRiskDataSource
{
    /// <summary>Сводка дашборда за период [<paramref name="from"/>, <paramref name="to"/>].</summary>
    Task<DashboardSummary> GetDashboardAsync(
        DateOnly from, DateOnly to, CancellationToken cancellationToken = default);
}
