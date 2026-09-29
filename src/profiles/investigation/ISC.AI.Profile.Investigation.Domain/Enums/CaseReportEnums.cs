namespace ISC.AI.Profile.Investigation.Domain.Enums;

/// <summary>Вид документа по бланку (ТФ-ДДЛ-04, ADR-0031).</summary>
public enum CaseReportKind
{
    /// <summary>«Сводка ОН» («Жыйынтык маалыматы»): хронология событий, адреса, маршруты, лица и транспорт.</summary>
    SummaryOn = 1,

    /// <summary>«Справка УН» («Аныктоо»): установочные данные, адреса, род занятий, связи, характеризующие материалы.</summary>
    ReferenceUn = 2,
}

/// <summary>
/// Состояние документа (ТФ-ДДЛ-05) — ВЫЧИСЛЯЕТСЯ из времени создания, окна и признака «активна», а не хранится:
/// фонового планировщика, который мог бы «не сработать», нет (ADR-0031 п. 4).
/// </summary>
public enum CaseReportState
{
    /// <summary>Окно редактирования открыто: правка свободная.</summary>
    Editable = 1,

    /// <summary>Окно закрыто: правка только по разрешению Администратора с причиной.</summary>
    Archived = 2,

    /// <summary>Помечен «неактивный» (вместо удаления): не правится, хранится.</summary>
    Inactive = 3,
}

/// <summary>Статус запроса на правку архивного документа (ТФ-АДМ-06).</summary>
public enum ReportEditPermitStatus
{
    /// <summary>Ждёт решения Администратора.</summary>
    Pending = 1,

    /// <summary>Разрешено (действует ограниченное время).</summary>
    Approved = 2,

    /// <summary>Отказано.</summary>
    Rejected = 3,
}

/// <summary>Русские подписи перечислений сводок и справок.</summary>
public static class CaseReportLabels
{
    /// <summary>Подпись вида документа.</summary>
    public static string Label(this CaseReportKind kind) => kind switch
    {
        CaseReportKind.SummaryOn => "Сводка ОН",
        CaseReportKind.ReferenceUn => "Справка УН",
        _ => kind.ToString(),
    };

    /// <summary>Подпись состояния документа.</summary>
    public static string Label(this CaseReportState state) => state switch
    {
        CaseReportState.Editable => "Редактируется",
        CaseReportState.Archived => "Архивный",
        CaseReportState.Inactive => "Неактивный",
        _ => state.ToString(),
    };

    /// <summary>Подпись статуса запроса на правку.</summary>
    public static string Label(this ReportEditPermitStatus status) => status switch
    {
        ReportEditPermitStatus.Pending => "Ждёт решения",
        ReportEditPermitStatus.Approved => "Разрешено",
        ReportEditPermitStatus.Rejected => "Отказано",
        _ => status.ToString(),
    };
}
