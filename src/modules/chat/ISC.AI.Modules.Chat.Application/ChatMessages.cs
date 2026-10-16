namespace ISC.AI.Modules.Chat.Application;

/// <summary>Тексты отказов чата (ADR-0052) — одни для всех профилей.</summary>
public static class ChatMessages
{
    /// <summary>Единый ответ «нет или нельзя» (ТБ-021): чужой диалог, недоступная и несуществующая область неразличимы.</summary>
    public const string NotFound = "Диалог или область чата не найдены либо недоступны.";

    /// <summary>Профиль не дал права пользоваться чатом.</summary>
    public const string NoRight = "Чат недоступен вашей роли (матрица доступа).";

    /// <summary>Нет числового идентификатора сотрудника — диалог не к кому привязать.</summary>
    public const string NoSubject = "Чат доступен только сотруднику с учётной записью.";

    /// <summary>В области нет материалов с текстом, а профиль причину не назвал.</summary>
    public const string NoMaterials = "В доступных материалах пока нечего читать.";

    /// <summary>Пустой вопрос.</summary>
    public const string EmptyQuestion = "Введите вопрос.";

    /// <summary>Узел ИИ не ответил.</summary>
    public const string ModelUnavailable = "Узел ИИ недоступен или не ответил вовремя — попробуйте позже.";

    /// <summary>Модель эмбеддингов не ответила при поиске по смыслу.</summary>
    public const string SenseUnavailable = "Узел ИИ (модель поиска по смыслу) недоступен — попробуйте позже.";

    /// <summary>Свободный режим профилем не включён.</summary>
    public const string FreeModeOff = "Свободный режим в этом чате не включён.";

    /// <summary>Поиск по смыслу профилем не включён.</summary>
    public const string SenseSearchOff = "Поиск по смыслу в этом чате не включён.";
}

/// <summary>Пределы чата (ADR-0052).</summary>
public static class ChatLimits
{
    /// <summary>Наибольшая длина заголовка диалога.</summary>
    public const int MaxTitleLength = 200;

    /// <summary>Наибольшая длина вопроса.</summary>
    public const int MaxQuestionLength = 4_000;

    /// <summary>Наибольшая длина хранимого ответа (длиннее — обрезается с пометкой).</summary>
    public const int MaxAnswerLength = 32_000;

    /// <summary>Наибольшая длина выдержки из фрагмента-источника, хранимой при ответе.</summary>
    public const int MaxExcerptLength = 400;

    /// <summary>Потолок числа фрагментов на вопрос и поиск, что бы ни задал профиль.</summary>
    public const int MaxTopK = 50;

    /// <summary>Потолок числа реплик истории, подаваемых модели.</summary>
    public const int MaxHistoryTurns = 40;
}
