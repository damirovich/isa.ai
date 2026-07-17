namespace ISC.AI.Profile.Inspector.Domain.Risk;

/// <summary>
/// Пороги перевода балла риска в уровни (Приложение §2): балл &lt; MediumFrom — низкий; [MediumFrom, HighFrom) —
/// средний; [HighFrom, CriticalFrom) — высокий; ≥ CriticalFrom — критический. Значения — СТАРТОВЫЕ (плейсхолдер),
/// калибруются на этапе Э1.
/// </summary>
/// <param name="MediumFrom">Порог «низкий → средний» (T1).</param>
/// <param name="HighFrom">Порог «средний → высокий» (T2).</param>
/// <param name="CriticalFrom">Порог «высокий → критический» (T3).</param>
public sealed record RiskThresholds(double MediumFrom, double HighFrom, double CriticalFrom)
{
    /// <summary>Стартовые пороги (плейсхолдер до калибровки на Э1).</summary>
    public static RiskThresholds Default { get; } = new(MediumFrom: 10, HighFrom: 25, CriticalFrom: 50);
}
