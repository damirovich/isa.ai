namespace ISC.AI.Profile.Inspector.Domain.Enums;

/// <summary>
/// Тяжесть нарушения — 4 уровня (Приложение §4 ТЗ, согласовано с экраном «Риски» прототипа).
/// Весовой коэффициент для формулы риска (низкая=1 / средняя=2 / высокая=4 / критическая=8) задаётся
/// в расчётчике риска (Приложение §2, шаг 2 Э5-01), а не в самом перечне — перечень остаётся чистой категорией.
/// </summary>
public enum ViolationSeverity
{
    /// <summary>Низкая.</summary>
    Low = 1,

    /// <summary>Средняя.</summary>
    Medium = 2,

    /// <summary>Высокая.</summary>
    High = 3,

    /// <summary>Критическая.</summary>
    Critical = 4,
}

/// <summary>Подписи по-русски — в домене, чтобы страницы не держали копий (как UserRoleLabels).</summary>
public static class ViolationSeverityLabels
{
    /// <summary>Подпись значения.</summary>
    public static string Label(this ViolationSeverity value) => value switch
    {
        ViolationSeverity.Low => "Низкая",
        ViolationSeverity.Medium => "Средняя",
        ViolationSeverity.High => "Высокая",
        ViolationSeverity.Critical => "Критическая",
        _ => value.ToString(),
    };
}
