namespace ISC.AI.Profile.Inspector.Domain.Enums;

/// <summary>
/// Статус редакции НПА (модель версионности профиля «ИнспекторAI», ТО-инф-04).
/// Доменное понятие профиля; в ядро не входит — там нейтральный флаг годности
/// <c>core.chunk.IsCurrent</c> (ADR-0013).
/// </summary>
public enum RevisionStatus
{
    /// <summary>Действующая редакция.</summary>
    Active = 0,

    /// <summary>Утратила силу.</summary>
    Repealed = 1,
}
