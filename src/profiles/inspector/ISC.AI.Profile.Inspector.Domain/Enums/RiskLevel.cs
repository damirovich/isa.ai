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

/// <summary>Подписи по-русски — в домене, чтобы страницы не держали копий (как UserRoleLabels).</summary>
public static class RiskLevelLabels
{
    /// <summary>Подпись значения.</summary>
    public static string Label(this RiskLevel value) => value switch
    {
        RiskLevel.Low => "Низкий",
        RiskLevel.Medium => "Средний",
        RiskLevel.High => "Высокий",
        RiskLevel.Critical => "Критический",
        _ => value.ToString(),
    };
}
