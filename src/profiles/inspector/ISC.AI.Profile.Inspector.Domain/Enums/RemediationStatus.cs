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
