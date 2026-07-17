namespace ISC.AI.Profile.Inspector.Domain.Risk;

/// <summary>Результат оценки риска подразделения (Приложение §2): балл, уровень и цвет светофора.</summary>
/// <param name="Score">Детерминированный балл риска (RiskScore).</param>
/// <param name="Level">Уровень (низкий/средний/высокий/критический).</param>
/// <param name="Color">Цвет дашборда (свёртка уровня в 3 цвета).</param>
public sealed record RiskAssessment(double Score, RiskLevel Level, RiskColor Color);
