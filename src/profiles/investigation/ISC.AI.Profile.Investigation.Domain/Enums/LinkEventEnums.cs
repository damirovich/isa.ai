namespace ISC.AI.Profile.Investigation.Domain.Enums;

/// <summary>Вид события — «дела-посредника» связи объектов (ТФ-ПЕР-11, ADR-0056).</summary>
public enum LinkEventKind
{
    /// <summary>Встреча.</summary>
    Meeting = 1,

    /// <summary>Контакт (звонок, переписка, передача).</summary>
    Contact = 2,

    /// <summary>Совместная поездка.</summary>
    Trip = 3,

    /// <summary>Иное.</summary>
    Other = 4,
}

/// <summary>
/// Уровень связи участника с событием [АИС ПМИ 3]: насколько достоверно участие объекта. «Неверно» остаётся на
/// карточке как история, но в граф, цепочку, поиск и пересечения не идёт (ADR-0056 п. 1).
/// </summary>
public enum LinkLevel
{
    /// <summary>Верно.</summary>
    Confirmed = 1,

    /// <summary>Предположительно.</summary>
    Probable = 2,

    /// <summary>Неверно.</summary>
    Wrong = 3,
}

/// <summary>Русские подписи перечислений событий.</summary>
public static class LinkEventEnumLabels
{
    /// <summary>Подпись вида события.</summary>
    public static string Label(this LinkEventKind kind) => kind switch
    {
        LinkEventKind.Meeting => "Встреча",
        LinkEventKind.Contact => "Контакт",
        LinkEventKind.Trip => "Совместная поездка",
        LinkEventKind.Other => "Иное",
        _ => kind.ToString(),
    };

    /// <summary>Подпись уровня связи (строчными — как в [АИС]: «верно / предположительно / неверно»).</summary>
    public static string Label(this LinkLevel level) => level switch
    {
        LinkLevel.Confirmed => "верно",
        LinkLevel.Probable => "предположительно",
        LinkLevel.Wrong => "неверно",
        _ => level.ToString(),
    };
}
