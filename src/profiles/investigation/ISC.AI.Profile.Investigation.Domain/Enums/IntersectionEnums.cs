namespace ISC.AI.Profile.Investigation.Domain.Enums;

/// <summary>Вид реквизита, по которому найдено пересечение между делами (ТФ-ПЕР-07, ADR-0029).</summary>
public enum IntersectionKind
{
    /// <summary>Госномер автотранспорта.</summary>
    Vehicle = 1,

    /// <summary>Адрес (строка адресов фигуранта или место жительства из анкеты).</summary>
    Address = 2,

    /// <summary>ФИО и дата рождения.</summary>
    PersonName = 3,
}

/// <summary>Решение человека по пересечению (ТФ-ПЕР-07): система связей между делами сама не создаёт.</summary>
public enum IntersectionDecision
{
    /// <summary>Пересечение подтверждено: это тот же автомобиль, адрес или человек.</summary>
    Confirmed = 1,

    /// <summary>Пересечение отклонено: совпадение случайное.</summary>
    Rejected = 2,
}

/// <summary>Русские подписи перечислений пересечений.</summary>
public static class IntersectionLabels
{
    /// <summary>Подпись вида реквизита.</summary>
    public static string Label(this IntersectionKind kind) => kind switch
    {
        IntersectionKind.Vehicle => "Автотранспорт",
        IntersectionKind.Address => "Адрес",
        IntersectionKind.PersonName => "ФИО и дата рождения",
        _ => kind.ToString(),
    };

    /// <summary>Подпись решения.</summary>
    public static string Label(this IntersectionDecision decision) => decision switch
    {
        IntersectionDecision.Confirmed => "Подтверждено",
        IntersectionDecision.Rejected => "Отклонено",
        _ => decision.ToString(),
    };
}
