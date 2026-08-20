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

/// <summary>
/// Человекочитаемые подписи статуса — в домене, чтобы каждая страница не держала свою копию
/// (тот же приём, что <c>UserRoleLabels</c>).
/// </summary>
public static class RevisionStatusLabels
{
    /// <summary>Подпись статуса по-русски.</summary>
    public static string Label(this RevisionStatus status) => status switch
    {
        RevisionStatus.Active => "Действующая",
        RevisionStatus.Repealed => "Утратила силу",
        _ => status.ToString(),
    };
}
