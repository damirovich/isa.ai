using ISC.AI.Profile.Inspector.Domain.Enums;
using ISC.AI.Profile.Inspector.Domain.Risk;
using Shouldly;

namespace ISC.AI.UnitTests.Profiles;

/// <summary>
/// Детерминированный расчёт риска (Э5-01 шаг 2, Приложение §2, §5.2.5.1): балл считает КОД по формуле,
/// результат воспроизводим на фиксированном наборе; веса тяжести 1/2/4/8; балл → уровень → 3 цвета.
/// </summary>
public sealed class RiskScoreCalculatorTests
{
    private readonly RiskScoreCalculator _calculator = new();

    [Fact(DisplayName = "Риск: балл точно по формуле Приложения §2 (веса по умолчанию)")]
    public void Computes_score_per_formula()
    {
        // Σseverity_weight = 8(критич)+2(средн)=10; w1·10 + w2·3 + w3·2 + w4·1 = 10 + 4.5 + 4 + 1 = 19.5.
        var signals = new RiskSignals(
            OpenSeverities: [ViolationSeverity.Critical, ViolationSeverity.Medium],
            RepeatCount: 3, OverdueCount: 2, TrendDelta: 1);

        _calculator.Assess(signals).Score.ShouldBe(19.5);
    }

    [Fact(DisplayName = "Риск: веса тяжести 1/2/4/8 (низкая/средняя/высокая/критическая)")]
    public void Severity_weights_are_1_2_4_8()
    {
        var signals = new RiskSignals(
            OpenSeverities:
            [
                ViolationSeverity.Low, ViolationSeverity.Medium, ViolationSeverity.High, ViolationSeverity.Critical,
            ],
            RepeatCount: 0, OverdueCount: 0, TrendDelta: 0);

        // Только w1·Σ = 1·(1+2+4+8) = 15.
        _calculator.Assess(signals).Score.ShouldBe(15);
    }

    [Theory(DisplayName = "Риск: балл → уровень → цвет по порогам")]
    [InlineData(5, RiskLevel.Low, RiskColor.Green)]
    [InlineData(10, RiskLevel.Medium, RiskColor.Yellow)]
    [InlineData(25, RiskLevel.High, RiskColor.Red)]
    [InlineData(50, RiskLevel.Critical, RiskColor.Red)]
    public void Maps_score_to_level_and_color(int score, RiskLevel expectedLevel, RiskColor expectedColor)
    {
        // Подбираем сигналы так, чтобы балл = score: только повторы с весом 1.
        var weights = new RiskWeights(OpenSeverity: 0, Repeat: 1, Overdue: 0, Trend: 0);
        var thresholds = new RiskThresholds(MediumFrom: 10, HighFrom: 25, CriticalFrom: 50);
        var signals = new RiskSignals(OpenSeverities: [], RepeatCount: score, OverdueCount: 0, TrendDelta: 0);

        var result = _calculator.Assess(signals, weights, thresholds);

        result.Level.ShouldBe(expectedLevel);
        result.Color.ShouldBe(expectedColor);
    }

    [Fact(DisplayName = "Риск: воспроизводимость — одни и те же входы дают тот же балл")]
    public void Reproducible_on_same_input()
    {
        var signals = new RiskSignals([ViolationSeverity.High], RepeatCount: 2, OverdueCount: 1, TrendDelta: 0);

        _calculator.Assess(signals).Score.ShouldBe(_calculator.Assess(signals).Score);
    }
}
