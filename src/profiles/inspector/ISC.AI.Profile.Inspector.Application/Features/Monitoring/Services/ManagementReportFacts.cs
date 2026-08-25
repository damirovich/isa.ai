using System.Globalization;
using System.Text;
using ISC.AI.Profile.Inspector.Domain.Enums;
using ISC.AI.Profile.Inspector.Domain.Services;

namespace ISC.AI.Profile.Inspector.Application.Features.Monitoring;

/// <summary>
/// ДЕТЕРМИНИРОВАННЫЙ факт-блок для отчёта руководству (ТФ-МОН-02): все числа отчёта считает КОД
/// (те же источники, что экраны Дашборда/Рисков/Мониторинга) — ИИ пишет только связующий текст
/// и не имеет других чисел, кроме этих (требование прописано в промпт-шаблоне).
/// </summary>
public static class ManagementReportFacts
{
    /// <summary>Собирает факт-блок из сводок периода.</summary>
    public static string Build(
        DashboardSummary dashboard,
        IReadOnlyList<DivisionRiskDetail> risks,
        IReadOnlyList<DivisionRemediationRow> remediation,
        int periodDays)
    {
        var facts = new StringBuilder();
        facts.AppendLine(CultureInfo.InvariantCulture,
            $"Период: последние {periodDays} дней.");
        facts.AppendLine(CultureInfo.InvariantCulture,
            $"Всего нарушений: {dashboard.TotalViolations}; критических: {dashboard.CriticalViolations}; "
            + $"устранено: {dashboard.Remediated}; не устранено: {dashboard.NotRemediated}.");

        facts.AppendLine("Риск по подразделениям (балл — детерминированная формула, Приложение §2):");
        if (risks.Count == 0)
        {
            facts.AppendLine("— нарушений за период не занесено, светофор пуст.");
        }

        foreach (var division in risks)
        {
            facts.AppendLine(CultureInfo.InvariantCulture,
                $"— {division.DivisionName}: уровень {division.Assessment.Level.Label()} "
                + $"(балл {division.Assessment.Score.ToString("0.#", CultureInfo.InvariantCulture)}); "
                + $"нарушений {division.ViolationCount}, открытых {division.OpenCount}, "
                + $"повторных {division.RepeatCount}, просроченных {division.OverdueCount}, "
                + $"динамика к прошлому периоду {(division.TrendDelta >= 0 ? "+" : string.Empty)}{division.TrendDelta}; "
                + $"болевые сферы: {(division.TopSpheres.Count > 0 ? string.Join(", ", division.TopSpheres) : "—")}.");
        }

        facts.AppendLine("Ход устранения по подразделениям:");
        foreach (var division in remediation)
        {
            facts.AppendLine(CultureInfo.InvariantCulture,
                $"— {division.DivisionName}: всего {division.Total}, устранено {division.Resolved}, "
                + $"частично {division.Partial}, на контроле {division.UnderControl}, просрочено {division.Overdue}.");
        }

        return facts.ToString();
    }
}
