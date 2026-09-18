namespace ISC.AI.Profile.Investigation.Domain.Enums;

/// <summary>
/// Роли профиля «Следствие» (ТП-004). Роль одна на пользователя; хранится в схеме профиля
/// (<c>investigation.user_role_assignment</c>), ядро о ролях не знает. Одно лицо не может быть
/// одновременно экспертом и верификатором одного результата — это правило модуля «Медиа» (ТБ-073),
/// а не роли.
/// </summary>
public enum InvestigationRole
{
    /// <summary>Учётные записи, роли, допуски, справочники, модели (ТФ-АДМ-01..03).</summary>
    Administrator = 1,

    /// <summary>Руководитель: дела подразделения, утверждение результатов, эскалации (ТФ-ДЕЛ-03, ТФ-ВЕР-02).</summary>
    Head = 2,

    /// <summary>Следователь: ведёт дела, загружает материалы, инициирует поиск в рамках дела.</summary>
    Investigator = 3,

    /// <summary>Эксперт по лицам: поиск и первичный разбор кандидат-листа (ТФ-ВЕР-01).</summary>
    FaceExpert = 4,

    /// <summary>Верификатор: второй независимый слепой разбор (ТФ-ВЕР-02).</summary>
    Verifier = 5,

    /// <summary>Офицер ИБ: чтение журнала аудита и метрик (ТФ-ВЕР-05).</summary>
    SecurityOfficer = 6,
}

/// <summary>Русские подписи ролей для интерфейса.</summary>
public static class InvestigationRoleLabels
{
    /// <summary>Подпись роли.</summary>
    public static string Label(this InvestigationRole role) => role switch
    {
        InvestigationRole.Administrator => "Администратор",
        InvestigationRole.Head => "Руководитель",
        InvestigationRole.Investigator => "Следователь",
        InvestigationRole.FaceExpert => "Эксперт по лицам",
        InvestigationRole.Verifier => "Верификатор",
        InvestigationRole.SecurityOfficer => "Офицер ИБ",
        _ => role.ToString(),
    };
}
