using System;
using System.Collections.Generic;
using System.Linq;
using ISC.AI.Abstractions.Conversations;
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

    /// <summary>
    /// Выбор области — карточками «помощников» (значок, название, назначение одной строкой) вместо выпадающего списка
    /// (ADR-0067). Для небольшого постоянного набора областей; у профиля с областями-делами — список, как раньше.
    /// </summary>
    public bool AreaCards { get; init; }

    /// <summary>Подпись ответа свободного режима под ответом: ответ не сверялся с материалами.</summary>
    public string FreeAnswerLabel { get; init; } = "Без проверки по материалам — проверьте сами";

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
/// <param name="OnlyMode">
/// Единственный режим ответа в области (ADR-0067): другой режим и поиск по смыслу сервер отклоняет, панель прячет
/// переключатель. Так профиль делает из областей «помощников» с одним понятным назначением («Ответ по документам»,
/// «Помощь с текстом»). Для области с поданными материалами (<see cref="ChatScope.Materials"/>) — обязательно
/// <see cref="ChatMode.Grounded"/>: поиск по смыслу шёл бы по корпусу, а не по ним. <see langword="null"/> — режимы
/// решают настройки профиля, как раньше.
/// </param>
/// <param name="Hint">Назначение области одной строкой — под названием в выборе и в пустом чате.</param>
/// <param name="Icon">Значок области в выборе карточками (<see cref="ChatSettings.AreaCards"/>) — смысловой, рисует панель пакета.</param>
/// <param name="AnswerLabel">
/// Подпись ответа по материалам этой области («По документам», «По справке»); <see langword="null"/> —
/// <see cref="ChatSettings.GroundedModeLabel"/>.
/// </param>
/// <param name="Redirects">
/// Подсказки «вопрос не туда»: если вопрос похож на вопрос другой области, панель до отправки предлагает спросить там.
/// </param>
public sealed record ChatArea(
    string? Key,
    string Title,
    string Description,
    short Classification,
    short AuditClassification,
    int? DivisionId,
    string AuditRef,
    IReadOnlyList<string> ScopeKeys,
    ChatLink? Link = null,
    ChatMode? OnlyMode = null,
    string? Hint = null,
    ChatAreaIcon Icon = ChatAreaIcon.Chat,
    string? AnswerLabel = null,
    IReadOnlyList<ChatRedirect>? Redirects = null);

/// <summary>Смысловой значок области (ADR-0067): профиль называет смысл, картинку выбирает панель пакета.</summary>
public enum ChatAreaIcon
{
    /// <summary>Чат вообще.</summary>
    Chat,

    /// <summary>Ответ по документам и материалам.</summary>
    Documents,

    /// <summary>Помощь с текстом без документов.</summary>
    Text,

    /// <summary>Справка о работе с системой.</summary>
    Help,
}

/// <summary>Ссылка на объект области.</summary>
/// <param name="Href">Адрес в интерфейсе.</param>
/// <param name="Text">Подпись кнопки.</param>
public sealed record ChatLink(string Href, string Text);

/// <summary>
/// Подсказка «вопрос задан не туда» (ADR-0067): вопрос похож на вопрос другой области — панель до отправки предлагает
/// перейти туда с тем же вопросом или отправить как есть. Только подсказка: решает сотрудник, сервер ничего не меняет.
/// </summary>
/// <param name="AreaKey">Куда предложить (ключ области; <see langword="null"/> — область по умолчанию).</param>
/// <param name="Message">Почему: «Этот режим не видит документы — похоже, нужен ответ по документам».</param>
/// <param name="Button">Подпись кнопки перехода: «Спросить по документам».</param>
/// <param name="Keywords">
/// Признаки вопроса без учёта регистра: слово — начало слова вопроса («закон» находит «законом», «законе»); фраза с
/// пробелом — подстрока вопроса («какой срок»).
/// </param>
public sealed record ChatRedirect(string? AreaKey, string Message, string Button, IReadOnlyList<string> Keywords)
{
    /// <summary>Похож ли вопрос на вопрос области назначения.</summary>
    public bool Matches(string question)
    {
        if (string.IsNullOrWhiteSpace(question) || Keywords.Count == 0)
        {
            return false;
        }

        var text = question.ToLowerInvariant().Replace('ё', 'е');
        var words = text.Split(
            text.Where(c => !char.IsLetterOrDigit(c)).Distinct().ToArray(),
            StringSplitOptions.RemoveEmptyEntries);

        foreach (var raw in Keywords)
        {
            var keyword = raw.Trim().ToLowerInvariant().Replace('ё', 'е');
            if (keyword.Length == 0)
            {
                continue;
            }

            var found = keyword.Contains(' ', StringComparison.Ordinal)
                ? text.Contains(keyword, StringComparison.Ordinal)
                : words.Any(word => word.StartsWith(keyword, StringComparison.Ordinal));
            if (found)
            {
                return true;
            }
        }

        return false;
    }
}

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
/// <param name="Materials">
/// Готовые фрагменты области ВМЕСТО поиска по корпусу (ADR-0067): например, разделы справки пользователя. Пакет подаёт
/// их ядру в <see cref="ISC.AI.Abstractions.Rag.GroundedRequest.Materials"/>; ядро пропускает только несекретные (гриф 0).
/// Область с поданными материалами объявляется с <see cref="ChatArea.OnlyMode"/> = <see cref="ChatMode.Grounded"/>. <see langword="null"/> — поиск
/// по корпусу (<see cref="DocumentIds"/>).
/// </param>
public sealed record ChatScope(
    IReadOnlyList<int>? DocumentIds,
    Func<RetrievedChunk, ChatSourceRef?> Describe,
    string? Coverage = null,
    string? CoverageHint = null,
    string? Unavailable = null,
    IReadOnlyList<RetrievedChunk>? Materials = null);

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
