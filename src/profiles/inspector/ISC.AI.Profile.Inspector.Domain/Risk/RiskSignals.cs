namespace ISC.AI.Profile.Inspector.Domain.Risk;

/// <summary>
/// Входные сигналы для расчёта риска подразделения за период (Приложение §2). Считаются ДЕТЕРМИНИРОВАННО
/// из нарушений (<c>inspector.violation</c>) и сроков поручений — код, не ИИ.
/// </summary>
/// <param name="OpenSeverities">Тяжести ОТКРЫТЫХ (неустранённых) нарушений — для объёма×тяжести (w1).</param>
/// <param name="RepeatCount">Число повторных нарушений: тот же вид в том же подразделении (w2).</param>
/// <param name="OverdueCount">Число просроченных устранений (w3).</param>
/// <param name="TrendDelta">Изменение числа нарушений к предыдущему периоду; может быть отрицательным (w4).</param>
public sealed record RiskSignals(
    IReadOnlyList<ViolationSeverity> OpenSeverities,
    int RepeatCount,
    int OverdueCount,
    int TrendDelta);
