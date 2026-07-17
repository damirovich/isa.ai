using System.Diagnostics.CodeAnalysis;

namespace ISC.AI.Profile.Inspector.Domain.Risk;

/// <summary>
/// Детерминированный расчёт уровня риска подразделения (Приложение §2 ТЗ, §5.2.5.1). Балл считает КОД, не
/// ИИ — ради прозрачности и воспроизводимости приёмки. Веса и пороги — конфигурируемы (калибруются на Э1).
/// </summary>
/// <remarks>
/// Формула: <c>RiskScore = w1·Σ severity_weight(открытые) + w2·повторы + w3·просрочки + w4·тренд</c>.
/// Фиксированный вес тяжести: низкая=1, средняя=2, высокая=4, критическая=8. Балл → уровень по порогам →
/// свёртка в 3 цвета (высокий+критический = красный). ИИ применяется только к тексту рекомендаций (отдельно),
/// сам балл — детерминирован.
/// </remarks>
[SuppressMessage("Performance", "CA1822:Mark members as static",
    Justification = "Инъектируемый доменный сервис (регистрируется в DI, внедряется в сценарии Рисков); экземплярные методы — намеренно, под будущую инъекцию конфигурации весов/порогов.")]
public sealed class RiskScoreCalculator
{
    /// <summary>Оценивает риск по сигналам с весами/порогами по умолчанию (стартовые, калибруются Э1).</summary>
    public RiskAssessment Assess(RiskSignals signals) =>
        Assess(signals, RiskWeights.Default, RiskThresholds.Default);

    /// <summary>Оценивает риск: балл по формуле (Приложение §2) → уровень по порогам → цвет.</summary>
    public RiskAssessment Assess(RiskSignals signals, RiskWeights weights, RiskThresholds thresholds)
    {
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(weights);
        ArgumentNullException.ThrowIfNull(thresholds);

        var openSeverityWeight = 0;
        foreach (var severity in signals.OpenSeverities)
        {
            openSeverityWeight += SeverityWeight(severity);
        }

        var score =
            (weights.OpenSeverity * openSeverityWeight)
            + (weights.Repeat * signals.RepeatCount)
            + (weights.Overdue * signals.OverdueCount)
            + (weights.Trend * signals.TrendDelta);

        var level = ToLevel(score, thresholds);
        return new RiskAssessment(score, level, ToColor(level));
    }

    /// <summary>Фиксированный вес тяжести (Приложение §2): низкая=1, средняя=2, высокая=4, критическая=8.</summary>
    private static int SeverityWeight(ViolationSeverity severity) => severity switch
    {
        ViolationSeverity.Low => 1,
        ViolationSeverity.Medium => 2,
        ViolationSeverity.High => 4,
        ViolationSeverity.Critical => 8,
        _ => 0,
    };

    private static RiskLevel ToLevel(double score, RiskThresholds t) =>
        score < t.MediumFrom ? RiskLevel.Low
        : score < t.HighFrom ? RiskLevel.Medium
        : score < t.CriticalFrom ? RiskLevel.High
        : RiskLevel.Critical;

    private static RiskColor ToColor(RiskLevel level) => level switch
    {
        RiskLevel.Low => RiskColor.Green,
        RiskLevel.Medium => RiskColor.Yellow,
        _ => RiskColor.Red, // высокий + критический → красный (Приложение §2)
    };
}
