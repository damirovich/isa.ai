using System.Globalization;
using ISC.AI.Abstractions.AI;
using ISC.AI.Abstractions.Conversations;
using ISC.AI.Abstractions.Retrieval;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Chat.Domain.Services;
using ISC.AI.Profile.Inspector.Application.Features.Guide;
using Scriban;

namespace ISC.AI.Profile.Inspector.Application.Features.Chat;

/// <summary>
/// Порт профиля «ИнспекторAI» для пакета «Чат» (ADR-0052) — «Чат-ассистент» (Э4-34). Три «помощника» — области с
/// единственным режимом, выбор карточками (ADR-0067): «Ответ по документам» — весь корпус, доступный инспектору по допуску,
/// общий грунтованный конвейер ядра без задачного промпта; «Помощь с текстом» — свободный ассистент ядра без документов;
/// «Как пользоваться системой» — ИИ-помощник по справке пользователя. Поиска по смыслу нет — для него есть «База НПА».
/// Вопрос «не туда» панель до отправки предлагает задать нужному помощнику (<see cref="ChatRedirect"/>).
/// </summary>
/// <remarks>
/// ИНВАРИАНТЫ (ТБ-020/040/041): область корпуса не сужает корпус — фильтр допуска к фрагментам применяет ядро; ссылки на
/// НПА извлекает и нормализует профиль (<c>NpaCitationExtractor</c>), проверяет ядровой валидатор по поданным фрагментам.
/// Метаданные фрагмента — «багаж» профиля: ключ <c>docflow_document_id</c> (его кладёт индексатор документооборота)
/// получает смысл здесь и в «Базе НПА» — карточка документа документооборота вместо карточки документа корпуса.
/// Область помощника НЕ ищет в корпусе: её материалы — разделы справки (гриф 0), поданные ядру вместо поиска
/// (<see cref="ChatScope.Materials"/>); справка в корпус не кладётся, чтобы Генератор и сверка не цитировали её как
/// источник (ADR-0067).
/// </remarks>
/// <param name="prompts">Провайдер задачных промптов профиля: шаблон помощника <c>chat-help.scriban</c>.</param>
public sealed class InspectorChatProfile(IPromptProvider prompts) : IChatProfile
{
    /// <summary>Объект журнала области: к нему пакет добавляет <c>:chat:{диалог}</c>.</summary>
    public const string AuditRef = "inspector";

    /// <summary>Ключ области ИИ-помощника «Как пользоваться системой» (адрес чата — <c>/chat?area=help</c>).</summary>
    public const string HelpAreaKey = "help";

    /// <summary>Ключ области «Помощь с текстом» — свободный ассистент без документов (адрес — <c>/chat?area=text</c>).</summary>
    public const string TextAreaKey = "text";

    // Название системы для промпта помощника; сам текст правил — в файле-шаблоне, а не в коде.
    private const string SystemName = "ИнспекторAI";

    // Ключи метаданных фрагмента справки: по ним подпись источника находит раздел и его страницу.
    private const string GuideSectionKey = "guide_section";
    private const string GuideRouteKey = "guide_route";

    // Задачный промпт помощника рендерится один раз: шаблон статичен, переменная — только название системы.
    private readonly string _helpPrompt =
        Template.Parse(prompts.GetTaskPrompt("chat-help").Text).Render(new { system = SystemName });

    // Выбор — тремя карточками «помощников» (ADR-0067): прежние «Где искать» + «Свободный чат / По документам» путали —
    // было непонятно, кто видит документы. Каждый помощник — область с ЕДИНСТВЕННЫМ режимом и своим назначением.
    private static readonly ChatSettings DefaultSettings = new()
    {
        Subtitle = "Ответ по документам, помощь с текстом и подсказки по работе с системой · история диалогов",
        AllowFreeMode = true,
        StartInFreeMode = false,
        AllowSenseSearch = false,
        GroundedModeLabel = "По документам",
        QuestionPlaceholder = "Ваш вопрос или задача…",
        AreaLabel = "Что нужно",
        AreaCards = true,
        FreeAnswerLabel = "Без документов — проверьте сами",
        TopK = 10,
        // Свободный режим — работа над текстом: прежний черновик ассистента должен уместиться в историю целиком.
        HistoryTurns = 10,
        HistoryTurnLength = 4_000,
    };

    // Признаки вопроса для подсказки «вопрос не туда» (ADR-0067). Слово — начало слова вопроса, фраза — подстрока.
    // Подобраны осторожно: подсказка лишь предлагает перейти, решает сотрудник («Всё равно отправить»).
    private static readonly string[] LawWords =
    [
        "закон", "кодекс", "статья", "статье", "статьи", "статью", "конституц", "постановлени", "нпа", "нормы", "норму",
        "норме", "нормативн", "штраф", "наказани", "какая ответственность", "ответственность за", "что говорит",
        "что сказано", "какой срок", "какие сроки", "в какой срок", "сколько дней",
    ];

    private static readonly string[] TextWords =
    [
        "перепиш", "переформулир", "помоги сформулировать", "сократи", "исправь", "отредактир", "составь план",
        "придумай", "напиши текст",
    ];

    private static readonly string[] SystemWords =
    [
        "кнопк", "нажать", "нажимать", "меню", "как пользоваться", "в каком разделе", "где найти раздел", "пароль",
        "войти в систему", "заблокир", "учетн",
    ];

    private static readonly ChatRedirect ToDocuments = new(
        AreaKey: null,
        Message: "Этот помощник не видит законы и документы. Похоже, нужен ответ по документам — со ссылками на источники.",
        Button: "Спросить по документам",
        Keywords: LawWords);

    private static readonly ChatRedirect ToText = new(
        AreaKey: TextAreaKey,
        Message: "«Ответ по документам» ищет в законах и документах. Переписать или составить текст лучше в «Помощи с текстом».",
        Button: "Попросить «Помощь с текстом»",
        Keywords: TextWords);

    private static readonly ChatRedirect ToHelp = new(
        AreaKey: HelpAreaKey,
        Message: "Похоже на вопрос о работе с системой — на него ответит помощник «Как пользоваться системой».",
        Button: "Спросить у помощника по системе",
        Keywords: SystemWords);

    // «Ответ по документам» — весь доступный корпус, только по найденным фрагментам: вопросы и диалоги без грифа (это
    // слова инспектора), ответы получают гриф использованных фрагментов от ядра. Гриф записи журнала о вопросе — допуск
    // сотрудника (см. CorpusFor). Область по умолчанию: прежние диалоги (и прежние свободные) остались в ней.
    private static readonly ChatArea Corpus = new(
        Key: null,
        Title: "Ответ по документам",
        Description: "документов и правовой базы, доступных вам",
        Classification: 0,
        AuditClassification: 0,
        DivisionId: null,
        AuditRef: AuditRef,
        ScopeKeys: [],
        OnlyMode: ChatMode.Grounded,
        Hint: "Ищет в законах и документах, отвечает со ссылками на источники.",
        Icon: ChatAreaIcon.Documents,
        AnswerLabel: "По документам",
        Redirects: [ToText, ToHelp]);

    // «Помощь с текстом» — прежний «Свободный чат» отдельной областью: формулировки, планы, черновики без документов
    // (свободный ассистент ядра; правило ядра запрещает ссылки на нормы «по памяти»). Своя история диалогов.
    private static readonly ChatArea Text = new(
        Key: TextAreaKey,
        Title: "Помощь с текстом",
        Description: "помощи с текстом без документов",
        Classification: 0,
        AuditClassification: 0,
        DivisionId: null,
        AuditRef: AuditRef + ":" + TextAreaKey,
        ScopeKeys: [],
        OnlyMode: ChatMode.Free,
        Hint: "Формулировки, планы, черновики. Документы и законы не видит — ссылок на нормы не даёт.",
        Icon: ChatAreaIcon.Text,
        Redirects: [ToDocuments, ToHelp]);

    // Область ИИ-помощника (ADR-0067): только вопросы по справке — свободный режим и поиск по смыслу в ней не нужны и
    // сервером отклоняются (единственный режим — по материалам). Вопросы — слова сотрудника о работе с системой (гриф 0); запись журнала о вопросе — по
    // допуску сотрудника, как в области корпуса (в вопрос могли вписать лишнее, см. HelpFor).
    private static readonly ChatArea Help = new(
        Key: HelpAreaKey,
        Title: "Как пользоваться системой",
        Description: "справки по работе с системой",
        Classification: 0,
        AuditClassification: 0,
        DivisionId: null,
        AuditRef: AuditRef + ":" + HelpAreaKey,
        ScopeKeys: [],
        Link: new ChatLink("/help", "Открыть «Помощь»"),
        OnlyMode: ChatMode.Grounded,
        Hint: "Подскажет, какой раздел открыть и что нажать. Отвечает только по справке.",
        Icon: ChatAreaIcon.Help,
        AnswerLabel: "По справке",
        Redirects: [ToDocuments]);

    // Разделы справки — материалы помощника, по фрагменту на раздел. Гриф 0 и «действует»: справка — описание
    // интерфейса поставки, не нормативный материал; ядро пропустит мимо поиска только такие фрагменты.
    private static readonly Lazy<IReadOnlyList<RetrievedChunk>> HelpMaterials = new(() =>
        [.. UserGuide.Current.Sections.Select((section, index) => new RetrievedChunk(
            ChunkId: index + 1,
            DocumentId: 0,
            Text: section.ToModelText(),
            Classification: 0,
            DivisionId: 0,
            IsCurrent: true,
            Score: 1.0,
            Metadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [GuideSectionKey] = section.Title,
                [GuideRouteKey] = section.Route ?? "/help",
            },
            DocumentTitle: "Справка «" + SystemName + "» · " + section.Title))]);

    // Типовые вопросы помощнику — по разделам, которые есть в справке.
    private static readonly ChatSuggestions HelpSuggestions = new()
    {
        Grounded =
        [
            "Как подготовить справку по итогам проверки?",
            "Как сверить проект приказа с законами?",
            "Как зарегистрировать документ и поставить поручение?",
            "Как сменить пароль?",
            "Что делать, если учётная запись заблокирована?",
        ],
    };

    // Примеры подобраны под то, что реально лежит в корпусе (кодексы, конституционные законы, внутренние инструкции):
    // щелчок по подсказке должен давать ответ со ссылками, а не «в документах не найдено».
    private static readonly ChatSuggestions DefaultSuggestions = new()
    {
        Free =
        [
            "Помоги сформулировать вывод по результатам проверки",
            "Составь план проверки подразделения на квартал",
            "Как кратко и ясно изложить замечание в справке?",
            "Перепиши официальным языком: «документы хранятся как попало, журнал учёта не ведётся»",
        ],
        Grounded =
        [
            "Какой испытательный срок можно установить при приёме на работу?",
            "Какая ответственность предусмотрена за разглашение государственной тайны?",
            "Какие полномочия есть у прокурора при проведении проверки?",
            "Какие ограничения Кодекс этики устанавливает для государственных служащих?",
            "Какие полномочия у Счетной палаты при проведении аудита?",
        ],
        Sense =
        [
            "испытательный срок при приёме на работу",
            "разглашение государственной тайны",
            "полномочия прокурора при проверке",
            "конфликт интересов государственного служащего",
            "порядок обращения с секретными документами",
        ],
    };

    /// <inheritdoc />
    public ChatSettings Settings => DefaultSettings;

    /// <inheritdoc />
    /// <remarks>Как и до пакета, чат-ассистент открыт каждому вошедшему: права на материалы решает допуск в ядре.</remarks>
    public Task<bool> CanUseAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

    /// <inheritdoc />
    public Task<IReadOnlyList<ChatArea>> ListAreasAsync(AccessContext access, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ChatArea>>([CorpusFor(access), TextFor(access), HelpFor(access)]);

    /// <inheritdoc />
    public Task<ChatArea?> ResolveAreaAsync(string? areaKey, AccessContext access, CancellationToken cancellationToken = default) =>
        Task.FromResult(
            string.IsNullOrWhiteSpace(areaKey) ? CorpusFor(access)
            : areaKey == TextAreaKey ? TextFor(access)
            : areaKey == HelpAreaKey ? HelpFor(access)
            : null);

    /// <inheritdoc />
    public Task<ChatScope> ResolveScopeAsync(
        ChatArea area, AccessContext access, bool forQuestion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(area);
        if (area.Key == TextAreaKey)
        {
            // Свободный режим материалы не читает; строка готовности лишь напоминает, что документов он не видит.
            return Task.FromResult(new ChatScope(DocumentIds: null, Describe, Coverage: "Документы не видит"));
        }

        if (!IsHelp(area))
        {
            return Task.FromResult(new ChatScope(DocumentIds: null, Describe));
        }

        var materials = HelpMaterials.Value;
        return Task.FromResult(new ChatScope(
            DocumentIds: null,
            DescribeHelp,
            Coverage: "Справка: разделов — " + materials.Count.ToString(CultureInfo.InvariantCulture),
            CoverageHint: "Помощник отвечает только по справке пользователя (раздел меню «Помощь»), без поиска в документах.",
            Materials: materials));
    }

    /// <inheritdoc />
    public string? TaskPrompt(ChatArea area) => area is not null && IsHelp(area) ? _helpPrompt : null;

    /// <inheritdoc />
    public ChatSuggestions Suggestions(ChatArea area) => area is not null && IsHelp(area) ? HelpSuggestions : DefaultSuggestions;

    private static bool IsHelp(ChatArea area) => area.Key == HelpAreaKey;

    // Как у области корпуса: запись журнала о вопросе — не ниже допуска сотрудника (ТБ-030/032).
    private static ChatArea TextFor(AccessContext access)
    {
        ArgumentNullException.ThrowIfNull(access);
        return Text with { AuditClassification = access.MaxClassification };
    }

    // Как у области корпуса: запись журнала о вопросе — не ниже допуска сотрудника (ТБ-030/032).
    private static ChatArea HelpFor(AccessContext access)
    {
        ArgumentNullException.ThrowIfNull(access);
        return Help with { AuditClassification = access.MaxClassification };
    }

    /// <summary>Подпись источника помощника: раздел справки и его страница (общие разделы — страница «Помощь»).</summary>
    public static ChatSourceRef? DescribeHelp(RetrievedChunk fragment)
    {
        ArgumentNullException.ThrowIfNull(fragment);
        return fragment.Metadata?.GetValueOrDefault(GuideSectionKey) is { Length: > 0 } section
            ? new ChatSourceRef("справка", section, fragment.Metadata.GetValueOrDefault(GuideRouteKey) ?? "/help")
            : null;
    }

    // ТБ-030/032: у корпуса нет своего грифа, а вопрос касается материалов вплоть до допуска сотрудника — запись журнала
    // классифицируется по допуску, как у сквозного AuditBehavior ядра (не ниже допуска субъекта), а не нулём.
    private static ChatArea CorpusFor(AccessContext access)
    {
        ArgumentNullException.ThrowIfNull(access);
        return Corpus with { AuditClassification = access.MaxClassification };
    }

    /// <summary>
    /// Подпись источника: документ документооборота — «№ рег. номер · заголовок» и его карточка; иначе — заголовок
    /// документа корпуса и карточка документа корпуса (ТФ-НПА-02).
    /// </summary>
    public static ChatSourceRef Describe(RetrievedChunk fragment)
    {
        ArgumentNullException.ThrowIfNull(fragment);
        var title = string.IsNullOrWhiteSpace(fragment.DocumentTitle)
            ? "Документ № " + fragment.DocumentId.ToString(CultureInfo.InvariantCulture)
            : fragment.DocumentTitle.Trim();

        if (fragment.Metadata?.GetValueOrDefault("docflow_document_id") is { Length: > 0 } raw
            && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var docflowId))
        {
            var label = fragment.Metadata.GetValueOrDefault("reg_number") is { Length: > 0 } number
                ? "№ " + number + " · " + title
                : title;
            return new ChatSourceRef("документ", label, "/docflow/documents/" + docflowId.ToString(CultureInfo.InvariantCulture));
        }

        return new ChatSourceRef(
            "документ корпуса", title, "/npa/documents/" + fragment.DocumentId.ToString(CultureInfo.InvariantCulture));
    }
}
