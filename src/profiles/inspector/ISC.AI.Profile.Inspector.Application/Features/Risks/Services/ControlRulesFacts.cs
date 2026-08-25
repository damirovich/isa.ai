using System.Globalization;
using System.Text;
using ISC.AI.Profile.Inspector.Domain.Enums;
using ISC.AI.Profile.Inspector.Domain.Services;

namespace ISC.AI.Profile.Inspector.Application.Features.Risks;

/// <summary>
/// ДЕТЕРМИНИРОВАННЫЙ факт-блок для проекта правил и критериев оценки (ТФ-РСК-02): перечень
/// показателей, которые СИСТЕМА реально считает (и только их), уровни риска и текущая картина
/// применения — модель оформляет это в правила, не изобретая метрик (запрет — в промпт-шаблоне).
/// </summary>
public static class ControlRulesFacts
{
    /// <summary>Собирает факт-блок: показатели системы + картина периода как пример применения.</summary>
    public static string Build(IReadOnlyList<DivisionRiskDetail> risks, int periodDays)
    {
        var facts = new StringBuilder();

        // Перечень показателей ЗАШИТ здесь, а не в промпте: он описывает то, что реально считает
        // код (RiskDataSource/RiskScoreCalculator, Приложение §2), и меняется вместе с ним.
        facts.AppendLine("Показатели, которые система считает по каждому подразделению (детерминированно):");
        facts.AppendLine("— нарушения за период (по данным учёта нарушений, с тяжестью и сферой);");
        facts.AppendLine("— открытые нарушения (устранение не завершено);");
        facts.AppendLine("— повторные нарушения (то же подразделение и тот же вид, что и ранее);");
        facts.AppendLine("— просроченные (контрольный срок устранения истёк — помечает система автоматически);");
        facts.AppendLine("— динамика к предыдущему периоду той же длины;");
        facts.AppendLine("— ход устранения: устранено / частично / на контроле / просрочено;");
        facts.AppendLine("— итоговый балл риска по детерминированной формуле (Приложение §2) "
            + "и уровень: Низкий, Средний, Высокий, Критический (светофор).");

        facts.AppendLine("Процедуры, которые система уже ведёт: занесение нарушений с классификатором "
            + "«сфера → вид», контрольные сроки устранения, автоматическая пометка просрочки, "
            + "мониторинг устранения, периодическая оценка риска подразделений.");

        facts.AppendLine(CultureInfo.InvariantCulture,
            $"Текущая картина применения (последние {periodDays} дней):");
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
                + $"повторных {division.RepeatCount}, просроченных {division.OverdueCount}.");
        }

        return facts.ToString();
    }
}
