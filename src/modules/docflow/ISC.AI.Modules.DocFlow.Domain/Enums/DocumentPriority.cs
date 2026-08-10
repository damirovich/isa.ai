namespace ISC.AI.Modules.DocFlow.Domain.Enums;

/// <summary>Приоритет документа (ТЗ СКИД §3.2, §9): только для группы «Исполнение».</summary>
public enum DocumentPriority
{
    /// <summary>Низкий.</summary>
    Low = 1,

    /// <summary>Средний.</summary>
    Medium = 2,

    /// <summary>Высокий.</summary>
    High = 3,
}
