namespace ISC.AI.Modules.DocFlow.Domain.Enums;

/// <summary>
/// Агрегированный статус документа (ТЗ СКИД §4.3): вычисляется по статусам ВСЕХ назначений
/// (<see cref="Services.AggregatedStatusCalculator"/>), вручную не выставляется.
/// </summary>
public enum DocumentAggregatedStatus
{
    /// <summary>Не применимо: группа «Хранение» либо «Исполнение» без назначений. В UI — прочерк.</summary>
    NotApplicable = 0,

    /// <summary>Зарегистрирован.</summary>
    Registered = 1,

    /// <summary>В работе.</summary>
    InProgress = 2,

    /// <summary>Частично исполнено.</summary>
    PartiallyDone = 3,

    /// <summary>Исполнено.</summary>
    Done = 4,

    /// <summary>Просрочено.</summary>
    Overdue = 5,

    /// <summary>Снято с контроля.</summary>
    Closed = 6,
}
