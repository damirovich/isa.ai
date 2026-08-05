namespace ISC.AI.Modules.DocFlow.Domain.Enums;

/// <summary>
/// Признак направленности документа (ТЗ СКИД §3.2): классификация и отчётность, на процесс не влияет.
/// В СКИД тип назывался <c>DirectionFlag</c> — переименован из-за CA1711 (суффикс «Flag»);
/// колонка БД осталась <c>direction_flag</c>.
/// </summary>
public enum DocumentDirection
{
    /// <summary>Входящий.</summary>
    Incoming = 1,

    /// <summary>Внутренний.</summary>
    Internal = 2,

    /// <summary>Исходящий.</summary>
    Outgoing = 3,
}
