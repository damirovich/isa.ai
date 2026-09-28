namespace ISC.AI.Profile.Investigation.Domain.Enums;

/// <summary>
/// Роль фигуранта в деле (ТФ-ПЕР-01): «объект» — лицо, за которым ведётся работа по заданию; «связь» —
/// лицо из окружения объекта (ТФ-ПЕР-06); «иная» — прочие фигуранты (подозреваемый, свидетель и т. п.
/// уточняются свободным текстом). «Неустановленное лицо» — не роль, а признак: неустановленным может
/// быть и объект, и связь.
/// </summary>
public enum PersonRole
{
    /// <summary>Объект задания.</summary>
    Target = 1,

    /// <summary>Связь объекта.</summary>
    Link = 2,

    /// <summary>Иная роль (уточняется свободным текстом).</summary>
    Other = 3,
}

/// <summary>Пол по анкете объекта (ТФ-ПЕР-05). Неизвестный пол — отсутствие значения, а не отдельный член.</summary>
public enum PersonSex
{
    /// <summary>Мужской.</summary>
    Male = 1,

    /// <summary>Женский.</summary>
    Female = 2,
}

/// <summary>
/// Вид справочника профиля (ТФ-АДМ-07): все справочники — одна таблица <c>investigation.reference_item</c>
/// с видом записи; ведёт Администратор, запись не удаляется, а выключается (на неё ссылаются дела).
/// </summary>
public enum ReferenceKind
{
    /// <summary>Подразделения-инициаторы заданий (ГУ) — ТФ-ДЕЛ-05, первый уровень архива после года.</summary>
    InitiatorUnit = 1,

    /// <summary>Специальные звания (инициатора задания и др.).</summary>
    Rank = 2,

    /// <summary>Должности (инициатора задания и др.).</summary>
    Position = 3,

    /// <summary>Типы связей объекта (родственная, деловая, иная) — ТФ-ПЕР-06.</summary>
    LinkType = 4,

    /// <summary>Категории материалов (для отчётов и обмена между офисами).</summary>
    MaterialCategory = 5,
}

/// <summary>Русские подписи перечислений фигурантов и справочников.</summary>
public static class PersonLabels
{
    /// <summary>Подпись роли фигуранта.</summary>
    public static string Label(this PersonRole role) => role switch
    {
        PersonRole.Target => "Объект",
        PersonRole.Link => "Связь",
        PersonRole.Other => "Иная",
        _ => role.ToString(),
    };

    /// <summary>Подпись пола.</summary>
    public static string Label(this PersonSex sex) => sex switch
    {
        PersonSex.Male => "Мужской",
        PersonSex.Female => "Женский",
        _ => sex.ToString(),
    };

    /// <summary>Подпись вида справочника (заголовок вкладки).</summary>
    public static string Label(this ReferenceKind kind) => kind switch
    {
        ReferenceKind.InitiatorUnit => "Подразделения-инициаторы (ГУ)",
        ReferenceKind.Rank => "Звания",
        ReferenceKind.Position => "Должности",
        ReferenceKind.LinkType => "Типы связей",
        ReferenceKind.MaterialCategory => "Категории материалов",
        _ => kind.ToString(),
    };
}
