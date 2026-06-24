namespace ISC.AI.Abstractions.Grounding;

/// <summary>Состояние проверки одной ссылки грунтовкой (ТБ-040, ADR-0005/0013).</summary>
public enum CitationStatus
{
    /// <summary>Подтверждена: найдена в АКТУАЛЬНОМ извлечённом фрагменте.</summary>
    Confirmed = 0,

    /// <summary>Не подтверждена: отсутствует в извлечённых фрагментах (возможна галлюцинация).</summary>
    Unverified = 1,

    /// <summary>
    /// Источник найден, но НЕ актуален (<c>IsCurrent = false</c>, утратил силу). Не выдаётся как
    /// действующая; в интерфейсе помечается «УТРАТИЛА СИЛУ» (ТЭ-003, ADR-0013).
    /// </summary>
    Superseded = 2,
}
