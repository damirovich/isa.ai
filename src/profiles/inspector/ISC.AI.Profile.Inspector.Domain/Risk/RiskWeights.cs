namespace ISC.AI.Profile.Inspector.Domain.Risk;

/// <summary>
/// Веса формулы риска (Приложение §2). Значения по умолчанию — СТАРТОВЫЕ, калибруются на этапе Э1.
/// </summary>
/// <param name="OpenSeverity">w1 — объём×тяжесть открытых нарушений.</param>
/// <param name="Repeat">w2 — повторяемость (тот же вид в том же подразделении).</param>
/// <param name="Overdue">w3 — просроченные устранения.</param>
/// <param name="Trend">w4 — рост числа нарушений к предыдущему периоду.</param>
public sealed record RiskWeights(
    double OpenSeverity = 1.0,
    double Repeat = 1.5,
    double Overdue = 2.0,
    double Trend = 1.0)
{
    /// <summary>Стартовые веса (w1=1.0, w2=1.5, w3=2.0, w4=1.0) — калибруются на Э1.</summary>
    public static RiskWeights Default { get; } = new();
}
