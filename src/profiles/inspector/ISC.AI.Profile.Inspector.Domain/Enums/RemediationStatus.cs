namespace ISC.AI.Profile.Inspector.Domain.Enums;

/// <summary>
/// Статус устранения нарушения (Приложение §4, согласовано с экраном «Мониторинг» прототипа).
/// </summary>
public enum RemediationStatus
{
    /// <summary>Устранено.</summary>
    Resolved = 0,

    /// <summary>На контроле.</summary>
    UnderControl = 1,

    /// <summary>Частично устранено.</summary>
    Partial = 2,

    /// <summary>Просрочено.</summary>
    Overdue = 3,
}

/// <summary>Подписи по-русски — в домене, чтобы страницы не держали копий (как UserRoleLabels).</summary>
public static class RemediationStatusLabels
{
    /// <summary>Подпись значения.</summary>
    public static string Label(this RemediationStatus value) => value switch
    {
        RemediationStatus.Resolved => "Устранено",
        RemediationStatus.UnderControl => "На контроле",
        RemediationStatus.Partial => "Частично устранено",
        RemediationStatus.Overdue => "Просрочено",
        _ => value.ToString(),
    };
}
