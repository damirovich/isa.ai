using ISC.AI.Modules.DocFlow.Domain.Enums;

namespace ISC.AI.Modules.DocFlow.Domain.Entities;

/// <summary>
/// Счётчик журнала регистрации (§3.2): отдельная нумерация на каждое направление в пределах года —
/// «Вх-{n}/{год}», «Исх-{n}/{год}», «Вн-{n}/{год}». Номер выдаётся атомарным UPSERT'ом на строке
/// счётчика (без гонок «max+1» при одновременной регистрации); выданный номер не переиспользуется —
/// после отказов возможны дыры в нумерации, что честнее совпадений.
/// </summary>
public class RegistrationCounter
{
    /// <summary>Направление журнала (входящие/внутренние/исходящие).</summary>
    public DocumentDirection Direction { get; set; }

    /// <summary>Год журнала (по дате регистрации документа).</summary>
    public int Year { get; set; }

    /// <summary>Последний выданный порядковый номер.</summary>
    public int LastNumber { get; set; }
}
