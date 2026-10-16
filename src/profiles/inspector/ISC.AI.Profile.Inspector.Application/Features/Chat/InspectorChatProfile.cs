using System.Globalization;
using ISC.AI.Abstractions.Retrieval;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Chat.Domain.Services;

namespace ISC.AI.Profile.Inspector.Application.Features.Chat;

/// <summary>
/// Порт профиля «ИнспекторAI» для пакета «Чат» (ADR-0052) — «Чат-ассистент» (Э4-34): одна область — весь корпус,
/// доступный инспектору по допуску; свободный режим разрешён и включён по умолчанию; режим «По документам» — общий
/// грунтованный конвейер ядра без задачного промпта (как до пакета); поиска по смыслу нет — для него есть «База НПА».
/// </summary>
/// <remarks>
/// ИНВАРИАНТЫ (ТБ-020/040/041): область не сужает корпус — фильтр допуска к фрагментам применяет ядро; ссылки на НПА
/// извлекает и нормализует профиль (<c>NpaCitationExtractor</c>), проверяет ядровой валидатор по поданным фрагментам.
/// Метаданные фрагмента — «багаж» профиля: ключ <c>docflow_document_id</c> (его кладёт индексатор документооборота)
/// получает смысл здесь и в «Базе НПА» — карточка документа документооборота вместо карточки документа корпуса.
/// </remarks>
public sealed class InspectorChatProfile : IChatProfile
{
    /// <summary>Объект журнала области: к нему пакет добавляет <c>:chat:{диалог}</c>.</summary>
    public const string AuditRef = "inspector";

    private static readonly ChatSettings DefaultSettings = new()
    {
        Subtitle = "Ассистент инспектора · история диалогов",
        AllowFreeMode = true,
        StartInFreeMode = true,
        AllowSenseSearch = false,
        GroundedModeLabel = "По документам",
        QuestionPlaceholder = "Ваш вопрос или задача…",
        EmptyHint = "«Свободный чат» — пояснения, формулировки, черновики без проверки по документам; «По документам» — ответ "
            + "только из найденных фрагментов, со ссылками.",
        TopK = 10,
        // Свободный режим — работа над текстом: прежний черновик ассистента должен уместиться в историю целиком.
        HistoryTurns = 10,
        HistoryTurnLength = 4_000,
    };

    // Одна область — весь доступный корпус: вопросы и диалоги без грифа (это слова инспектора), ответы получают гриф
    // использованных фрагментов от ядра. Гриф записи журнала о вопросе — допуск сотрудника (см. CorpusFor).
    private static readonly ChatArea Corpus = new(
        Key: null,
        Title: "Документы и правовая база",
        Description: "документов и правовой базы, доступных вам",
        Classification: 0,
        AuditClassification: 0,
        DivisionId: null,
        AuditRef: AuditRef,
        ScopeKeys: []);

    private static readonly ChatSuggestions DefaultSuggestions = new()
    {
        Free =
        [
            "Помоги сформулировать вывод по результатам проверки",
            "Составь план проверки подразделения",
            "Как кратко и ясно изложить замечание в справке?",
        ],
        Grounded =
        [
            "Какие нарушения выявлялись при проверках?",
            "Какие сроки установлены для устранения нарушений?",
            "Какие требования предъявляются к проведению проверок?",
        ],
    };

    /// <inheritdoc />
    public ChatSettings Settings => DefaultSettings;

    /// <inheritdoc />
    /// <remarks>Как и до пакета, чат-ассистент открыт каждому вошедшему: права на материалы решает допуск в ядре.</remarks>
    public Task<bool> CanUseAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

    /// <inheritdoc />
    public Task<IReadOnlyList<ChatArea>> ListAreasAsync(AccessContext access, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ChatArea>>([CorpusFor(access)]);

    /// <inheritdoc />
    public Task<ChatArea?> ResolveAreaAsync(string? areaKey, AccessContext access, CancellationToken cancellationToken = default) =>
        Task.FromResult(string.IsNullOrWhiteSpace(areaKey) ? CorpusFor(access) : null);

    /// <inheritdoc />
    public Task<ChatScope> ResolveScopeAsync(
        ChatArea area, AccessContext access, bool forQuestion, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ChatScope(DocumentIds: null, Describe));

    /// <inheritdoc />
    public string? TaskPrompt(ChatArea area) => null;

    /// <inheritdoc />
    public ChatSuggestions Suggestions(ChatArea area) => DefaultSuggestions;

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
