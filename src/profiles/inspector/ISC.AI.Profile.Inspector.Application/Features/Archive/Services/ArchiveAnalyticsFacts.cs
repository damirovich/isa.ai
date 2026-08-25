using System.Globalization;
using System.Text;
using ISC.AI.Profile.Inspector.Domain.Enums;
using ISC.AI.Profile.Inspector.Domain.Services;

namespace ISC.AI.Profile.Inspector.Application.Features.Archive;

/// <summary>
/// ДЕТЕРМИНИРОВАННЫЙ факт-блок для ИИ-аналитики архива (ТФ-АРХ-03): тренды (текущий период против
/// предыдущего), повторяемость и разрез «территориальные/линейные» считает КОД — модель только
/// интерпретирует закономерности (запрет других чисел — в промпт-шаблоне).
/// </summary>
public static class ArchiveAnalyticsFacts
{
    /// <summary>Собирает факт-блок из сводок двух смежных периодов и справочника типов подразделений.</summary>
    /// <param name="current">Сводка текущего периода.</param>
    /// <param name="previous">Сводка предыдущего периода той же длины (база тренда).</param>
    /// <param name="risks">Разбор риска по подразделениям за текущий период (повторы, просрочки).</param>
    /// <param name="remediation">Ход устранения по подразделениям за текущий период.</param>
    /// <param name="kinds">Тип подразделения по идентификатору (справочник §4.2).</param>
    /// <param name="periodDays">Длина периода в днях.</param>
    public static string Build(
        DashboardSummary current,
        DashboardSummary previous,
        IReadOnlyList<DivisionRiskDetail> risks,
        IReadOnlyList<DivisionRemediationRow> remediation,
        IReadOnlyDictionary<int, DivisionKind> kinds,
        int periodDays)
    {
        var facts = new StringBuilder();

        facts.AppendLine(CultureInfo.InvariantCulture,
            $"Период анализа: последние {periodDays} дней; сравнение — с предыдущими {periodDays} днями.");
        facts.AppendLine(CultureInfo.InvariantCulture,
            $"Текущий период: всего нарушений {current.TotalViolations} (критических {current.CriticalViolations}); "
            + $"устранено {current.Remediated}, не устранено {current.NotRemediated}.");
        facts.AppendLine(CultureInfo.InvariantCulture,
            $"Предыдущий период: всего нарушений {previous.TotalViolations} (критических {previous.CriticalViolations}); "
            + $"устранено {previous.Remediated}, не устранено {previous.NotRemediated}.");
        facts.AppendLine(CultureInfo.InvariantCulture,
            $"Изменение к предыдущему периоду: нарушений {Signed(current.TotalViolations - previous.TotalViolations)}, "
            + $"критических {Signed(current.CriticalViolations - previous.CriticalViolations)}.");

        facts.AppendLine("Повторяемость (повтор — то же подразделение и тот же вид нарушения, что раньше):");
        var repeaters = risks.Where(r => r.RepeatCount > 0).ToList();
        if (repeaters.Count == 0)
        {
            facts.AppendLine("— повторных нарушений за период не выявлено.");
        }

        foreach (var division in repeaters)
        {
            facts.AppendLine(CultureInfo.InvariantCulture,
                $"— {division.DivisionName}: повторных {division.RepeatCount} из {division.ViolationCount}; "
                + $"болевые сферы: {(division.TopSpheres.Count > 0 ? string.Join(", ", division.TopSpheres) : "—")}.");
        }

        AppendKindComparison(facts, risks, remediation, kinds);

        facts.AppendLine("Разбор по подразделениям (текущий период):");
        if (risks.Count == 0)
        {
            facts.AppendLine("— нарушений за период не занесено.");
        }

        foreach (var division in risks)
        {
            facts.AppendLine(CultureInfo.InvariantCulture,
                $"— {division.DivisionName} ({KindOf(kinds, division.DivisionId).Label().ToLowerInvariant()}): "
                + $"нарушений {division.ViolationCount}, открытых {division.OpenCount}, повторных {division.RepeatCount}, "
                + $"просроченных {division.OverdueCount}, динамика {Signed(division.TrendDelta)}.");
        }

        return facts.ToString();
    }

    /// <summary>Сравнение территориальных и линейных подразделений — суть ТФ-АРХ-03.</summary>
    private static void AppendKindComparison(
        StringBuilder facts,
        IReadOnlyList<DivisionRiskDetail> risks,
        IReadOnlyList<DivisionRemediationRow> remediation,
        IReadOnlyDictionary<int, DivisionKind> kinds)
    {
        facts.AppendLine("Сравнение территориальных и линейных подразделений (текущий период):");

        // Честность разреза: если линейных в справочнике не помечено, сравнение вырождается —
        // и это надо сказать модели прямо, а не дать ей додумывать вторую группу.
        if (!kinds.Values.Any(k => k == DivisionKind.Linear))
        {
            facts.AppendLine(
                "— линейные подразделения в справочнике не помечены (все записи — территориальные); "
                + "сравнение групп невозможно, тип задаётся на странице «Подразделения».");
            return;
        }

        foreach (var kind in new[] { DivisionKind.Territorial, DivisionKind.Linear })
        {
            var kindRisks = risks.Where(r => KindOf(kinds, r.DivisionId) == kind).ToList();
            var kindRemediation = remediation.Where(r => KindOf(kinds, r.DivisionId) == kind).ToList();
            facts.AppendLine(CultureInfo.InvariantCulture,
                $"— {kind.Label()}: подразделений с нарушениями {kindRisks.Count}; "
                + $"нарушений {kindRisks.Sum(r => r.ViolationCount)}, открытых {kindRisks.Sum(r => r.OpenCount)}, "
                + $"повторных {kindRisks.Sum(r => r.RepeatCount)}, просроченных {kindRisks.Sum(r => r.OverdueCount)}; "
                + $"устранено {kindRemediation.Sum(r => r.Resolved)} из {kindRemediation.Sum(r => r.Total)}.");
        }
    }

    // Подразделение без записи в справочнике трактуем территориальным — историческое умолчание модели.
    private static DivisionKind KindOf(IReadOnlyDictionary<int, DivisionKind> kinds, int divisionId) =>
        kinds.TryGetValue(divisionId, out var kind) ? kind : DivisionKind.Territorial;

    private static string Signed(int value) => value >= 0 ? $"+{value}" : value.ToString(CultureInfo.InvariantCulture);
}
