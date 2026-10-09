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

    /// <summary>
    /// Задание по объекту (ТФ-ДЕЛ-01/05): работа за лицом по поручению подразделения-инициатора (ГУ).
    /// Только у этого вида есть реквизиты задания, и у него они обязательны (инвариант хранилища и БД).
    /// </summary>
    ObjectTask = 4,
}

/// <summary>
/// Направление работы — отдел ОПУ (ТЭ-008, ТФ-ДЕЛ-01; ТЗ Заказчика §1.3, §2.1). Отметкой направления помечается
/// ПОДРАЗДЕЛЕНИЕ справочника (вложенные получают её от вышестоящего), а дело относится к отделу через своё
/// подразделение — отдельного поля у дела нет (ADR-0039). Видимость дел решает допуск по подразделениям, гриф и роль
/// (ТБ-020); отметка сама доступа не открывает и не закрывает. ОТМ — отдельный экземпляр (ТС-014) и сюда не входит.
/// </summary>
public enum CaseDirection
{
    /// <summary>ОН — оперативное наблюдение.</summary>
    Surveillance = 1,

    /// <summary>ОУ — оперативная установка.</summary>
    Establishment = 2,
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

    /// <summary>
    /// Отозвано как ошибочное (ADR-0034): строка остаётся для истории, но в пересечениях, подсказках эксперту и счётчиках
    /// не участвует.
    /// </summary>
    Revoked = 2,
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
        CaseKind.ObjectTask => "Задание по объекту",
        _ => kind.ToString(),
    };

    /// <summary>Краткая подпись направления: «ОН», «ОУ».</summary>
    public static string ShortLabel(this CaseDirection direction) => direction switch
    {
        CaseDirection.Surveillance => "ОН",
        CaseDirection.Establishment => "ОУ",
        _ => direction.ToString(),
    };

    /// <summary>Полное название направления: «Оперативное наблюдение», «Оперативная установка».</summary>
    public static string Label(this CaseDirection direction) => direction switch
    {
        CaseDirection.Surveillance => "Оперативное наблюдение",
        CaseDirection.Establishment => "Оперативная установка",
        _ => direction.ToString(),
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
