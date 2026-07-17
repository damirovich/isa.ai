namespace ISC.AI.Profile.Inspector.Domain.Enums;

/// <summary>
/// Уровень риска подразделения (Приложение §2 ТЗ): 4 уровня по детерминированному баллу RiskScore.
/// Для дашборда сворачивается в 3 цвета (<see cref="RiskColor"/>).
/// </summary>
public enum RiskLevel
{
    /// <summary>Низкий.</summary>
    Low = 0,

    /// <summary>Средний.</summary>
    Medium = 1,

    /// <summary>Высокий.</summary>
    High = 2,

    /// <summary>Критический.</summary>
    Critical = 3,
}
