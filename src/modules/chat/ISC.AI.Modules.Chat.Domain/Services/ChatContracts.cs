using ISC.AI.Abstractions.Retrieval;

namespace ISC.AI.Modules.Chat.Domain.Services;

// Контракты порта IChatProfile (ADR-0052). Разрез «интерфейс отдельно, контракты рядом» — как в пакетах docflow и media.

/// <summary>Настройки чата профиля (ADR-0052). Значения по умолчанию — чат только по материалам, без поиска по смыслу.</summary>
public sealed record ChatSettings
{
    /// <summary>Подзаголовок страницы чата (заголовок и значок — из записи реестра профиля).</summary>
    public string? Subtitle { get; init; }

    /// <summary>Разрешён «Свободный чат» — ассистент без поиска и грунтовки (правило ядра запрещает ссылки «по памяти»).</summary>
    public bool AllowFreeMode { get; init; }

    /// <summary>Открывать чат в свободном режиме (только при <see cref="AllowFreeMode"/>).</summary>
    public bool StartInFreeMode { get; init; }

    /// <summary>Разрешён режим «Найти по смыслу» — сами фрагменты без модели.</summary>
    public bool AllowSenseSearch { get; init; }

    /// <summary>Подпись режима по материалам: «По документам», «Спросить» и т.п.</summary>
    public string GroundedModeLabel { get; init; } = "По материалам";

    /// <summary>Подсказка в поле вопроса.</summary>
    public string QuestionPlaceholder { get; init; } = "Ваш вопрос…";

    /// <summary>Пояснение в пустом чате: что за материалы и как отвечает чат; <see langword="null"/> — без пояснения.</summary>
    public string? EmptyHint { get; init; }

    /// <summary>Подпись выбора области на странице (виден, если областей больше одной).</summary>
    public string AreaLabel { get; init; } = "Где искать";

    /// <summary>Сколько фрагментов подаётся модели на один вопрос.</summary>
    public int TopK { get; init; } = 10;

    /// <summary>Сколько последних реплик диалога подаётся модели как история.</summary>
    public int HistoryTurns { get; init; } = 6;

    /// <summary>Наибольшая длина одной реплики истории, подаваемой модели.</summary>
    public int HistoryTurnLength { get; init; } = 1_500;

    /// <summary>Сколько фрагментов выдаёт поиск по смыслу.</summary>
    public int SenseSearchTopK { get; init; } = 20;
}

/// <summary>
/// Область чата — где спрашивать (ADR-0052): дело, все доступные дела, весь корпус. Ключ и смысл знает только профиль.
/// </summary>
/// <param name="Key">Ключ области (например, <c>case:45</c>); <see langword="null"/> — область профиля по умолчанию.</param>
/// <param name="Title">Название для выбора: «Все мои дела (7)», «№ 45 — Сбыт».</param>
/// <param name="Description">Область словами для промпта и журнала: «дела № 45 «Сбыт»».</param>
/// <param name="Classification">Гриф вопросов и диалогов области (например, гриф дела; для общей области — 0).</param>
/// <param name="AuditClassification">Гриф записи журнала о вопросе — наибольший гриф области.</param>
/// <param name="DivisionId">Подразделение области для журнала; <see langword="null"/> — не одно.</param>
/// <param name="AuditRef">Объект журнала области: к нему пакет добавляет <c>:chat:{диалог}</c>.</param>
/// <param name="ScopeKeys">Ключи уничтожения области: диалог уйдёт с этим объектом (ТБ-064).</param>
/// <param name="Link">Ссылка на объект области («Карточка дела»); <see langword="null"/> — нет.</param>
public sealed record ChatArea(
    string? Key,
    string Title,
    string Description,
    short Classification,
    short AuditClassification,
    int? DivisionId,
    string AuditRef,
    IReadOnlyList<string> ScopeKeys,
    ChatLink? Link = null);

/// <summary>Ссылка на объект области.</summary>
/// <param name="Href">Адрес в интерфейсе.</param>
/// <param name="Text">Подпись кнопки.</param>
public sealed record ChatLink(string Href, string Text);

/// <summary>
/// Материалы области (ADR-0052): какие документы корпуса ядра искать и как подписать найденное.
/// </summary>
/// <param name="DocumentIds">
/// Документы корпуса ядра — область поиска (<see cref="RetrievalFilter.DocumentIds"/>); <see langword="null"/> — весь
/// доступный субъекту корпус; пусто — читать нечего.
/// </param>
/// <param name="Describe">
/// Подпись найденного фрагмента: вид, подпись, адрес, ключ уничтожения; <see langword="null"/> — фрагмент не из
/// материалов области (в источники не попадает).
/// </param>
/// <param name="Coverage">Строка «что видит чат»; <see langword="null"/> — не показывать.</param>
/// <param name="CoverageHint">Всплывающее пояснение к строке готовности.</param>
/// <param name="Unavailable">
/// Почему спрашивать нельзя («нет доступных дел», «нечего читать»); <see langword="null"/> — можно. При отказе модель не
/// вызывается.
/// </param>
public sealed record ChatScope(
    IReadOnlyList<int>? DocumentIds,
    Func<RetrievedChunk, ChatSourceRef?> Describe,
    string? Coverage = null,
    string? CoverageHint = null,
    string? Unavailable = null);

/// <summary>Чем является найденный фрагмент для сотрудника.</summary>
/// <param name="Kind">Вид словами: «документ», «расшифровка», «сводка/справка».</param>
/// <param name="Label">Подпись источника (для расшифровки — вместе с моментом записи).</param>
/// <param name="Href">Что открыть; <see langword="null"/> — без ссылки.</param>
/// <param name="ScopeKey">Ключ уничтожения материала (например, дело); диалог уйдёт вместе с ним (ТБ-064).</param>
public sealed record ChatSourceRef(string Kind, string Label, string? Href, string? ScopeKey = null);

/// <summary>Подсказки — готовые вопросы по режимам (видны всегда, щелчок задаёт вопрос сразу).</summary>
public sealed record ChatSuggestions
{
    /// <summary>Нет подсказок.</summary>
    public static ChatSuggestions None { get; } = new();

    /// <summary>Вопросы режима по материалам.</summary>
    public IReadOnlyList<string> Grounded { get; init; } = [];

    /// <summary>Вопросы свободного режима.</summary>
    public IReadOnlyList<string> Free { get; init; } = [];

    /// <summary>Запросы поиска по смыслу.</summary>
    public IReadOnlyList<string> Sense { get; init; } = [];
}
