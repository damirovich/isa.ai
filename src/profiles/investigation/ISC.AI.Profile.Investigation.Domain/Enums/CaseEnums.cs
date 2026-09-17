namespace ISC.AI.Profile.Investigation.Domain.Enums;

/// <summary>Вид дела (ТФ-ДЕЛ-01).</summary>
public enum CaseKind
{
    /// <summary>Уголовное дело.</summary>
    CriminalCase = 1,

    /// <summary>Материал (доследственная проверка).</summary>
    Material = 2,

    /// <summary>Оперативно-розыскное мероприятие (Закон об ОРД № 127).</summary>
    OperativeMeasure = 3,
}

/// <summary>Статус дела (ТФ-ДЕЛ-01). Закрытие запускает регламент удаления шаблонов (ТФ-ДЕЛ-04, ТБ-074).</summary>
public enum CaseStatus
{
    /// <summary>В производстве.</summary>
    InProgress = 1,

    /// <summary>Приостановлено.</summary>
    Suspended = 2,

    /// <summary>Закрыто.</summary>
    Closed = 3,
}

/// <summary>Вид основания поиска (ТБ-071): без основания поиск технически невозможен.</summary>
public enum AuthorizationKind
{
    /// <summary>Поручение следователя.</summary>
    InvestigatorOrder = 1,

    /// <summary>Постановление.</summary>
    Resolution = 2,

    /// <summary>Номер ОРМ.</summary>
    OperativeMeasure = 3,
}

/// <summary>Статус появления фигуранта (ТФ-ВЕР-03): система выдаёт только «следственную версию».</summary>
public enum AppearanceStatus
{
    /// <summary>Следственная версия — требует процессуальной проверки (УПК ст. 209–211, 94).</summary>
    InvestigativeLead = 1,
}

/// <summary>Русские подписи перечислений дел.</summary>
public static class CaseLabels
{
    /// <summary>Подпись вида дела.</summary>
    public static string Label(this CaseKind kind) => kind switch
    {
        CaseKind.CriminalCase => "Уголовное дело",
        CaseKind.Material => "Материал",
        CaseKind.OperativeMeasure => "ОРМ",
        _ => kind.ToString(),
    };

    /// <summary>Подпись статуса дела.</summary>
    public static string Label(this CaseStatus status) => status switch
    {
        CaseStatus.InProgress => "В производстве",
        CaseStatus.Suspended => "Приостановлено",
        CaseStatus.Closed => "Закрыто",
        _ => status.ToString(),
    };

    /// <summary>Подпись вида основания.</summary>
    public static string Label(this AuthorizationKind kind) => kind switch
    {
        AuthorizationKind.InvestigatorOrder => "Поручение следователя",
        AuthorizationKind.Resolution => "Постановление",
        AuthorizationKind.OperativeMeasure => "ОРМ",
        _ => kind.ToString(),
    };
}
