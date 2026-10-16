using System.Globalization;
using ISC.AI.Abstractions.Retrieval;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Chat.Domain.Services;
using ISC.AI.Modules.Media.Domain.Services;
using ISC.AI.Profile.Investigation.Domain.Services;

namespace ISC.AI.Profile.Investigation.Application.Features.CaseChat;

/// <summary>Ключи областей чата «Следствия» (ADR-0052): дело — <c>case:{id}</c>; он же ключ уничтожения переписки с делом.</summary>
public static class CaseChatAreas
{
    /// <summary>Приставка ключа дела.</summary>
    public const string CasePrefix = "case:";

    /// <summary>Ключ области (и ключ уничтожения) дела.</summary>
    public static string KeyOf(int caseId) => CasePrefix + caseId.ToString(CultureInfo.InvariantCulture);

    /// <summary>Дело по ключу области; <see langword="false"/> — ключ не дела.</summary>
    public static bool TryParse(string? key, out int caseId)
    {
        caseId = 0;
        return key is not null
            && key.StartsWith(CasePrefix, StringComparison.Ordinal)
            && int.TryParse(key.AsSpan(CasePrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out caseId)
            && caseId > 0;
    }
}

/// <summary>Тексты чата по материалам дел (ADR-0051).</summary>
public static class CaseChatTexts
{
    /// <summary>В области нет ни одного материала с текстом в корпусе.</summary>
    public const string NoMaterials =
        "В доступных материалах пока нечего читать: прикрепите документы к делу, дождитесь расшифровки записей или заведите сводку.";

    /// <summary>Нет ни одного доступного дела.</summary>
    public const string NoCases = "У вас нет доступных дел — спрашивать не по чему.";

    /// <summary>Дело области стало недоступно между выбором и вопросом.</summary>
    public const string CaseGone = "Дело не найдено или недоступно.";
}

/// <summary>
/// Порт профиля «Следствие» для пакета «Чат» (ADR-0052): области — все доступные дела (по умолчанию) или одно дело;
/// материалы — документы дел, расшифровки записей, сводки и справки, «Сведения дела» (ADR-0051); право — «Чат по
/// материалам дел» матрицы доступа. Свободного режима нет, поиск по смыслу есть.
/// </summary>
/// <remarks>
/// ИНВАРИАНТЫ (ТБ-020/021/071): дела областей — только видимые по полной решётке (<see cref="ICaseChatScopes"/>);
/// материалы — под решётками документооборота и «Медиа» (<see cref="ChatMaterialResolver"/>); область, ставшая
/// невидимой, — <see langword="null"/>, и пакет закрывает её диалоги. Задачный промпт — файл-шаблон
/// <c>case-chat.scriban</c>, второе системное сообщение после правила грунтовки ядра (ТБ-041). Ключ уничтожения
/// источника — дело (<see cref="CaseChatAreas"/>): переписка, опиравшаяся на материалы дела, уходит вместе с ним.
/// </remarks>
public sealed class InvestigationChatProfile(
    ICaseChatScopes scopes,
    ChatMaterialResolver materials,
    IUserRoleStore roles,
    ISubjectProvider subjectProvider) : IChatProfile
{
    /// <summary>Объект журнала для области «все дела»: к нему пакет добавляет <c>:chat:{диалог}</c>.</summary>
    public const string AuditRefAll = "investigation";

    private static readonly ChatSettings DefaultSettings = new()
    {
        Subtitle = "Вопросы к документам, расшифровкам, сводкам и сведениям ваших дел — ответ только по найденному, со ссылками",
        AllowFreeMode = false,
        AllowSenseSearch = true,
        GroundedModeLabel = "Спросить",
        QuestionPlaceholder = "Ваш вопрос по материалам…",
        EmptyHint = "Документы, расшифровки записей, сводки и справки, сведения о фигурантах. Ответ — только по найденному, со ссылками.",
        TopK = 10,
        HistoryTurns = 6,
        HistoryTurnLength = 1_500,
        SenseSearchTopK = 20,
    };

    /// <inheritdoc />
    public ChatSettings Settings => DefaultSettings;

    /// <inheritdoc />
    public Task<bool> CanUseAsync(CancellationToken cancellationToken = default) =>
        PermissionRule.CallerHasAsync(roles, subjectProvider, InvestigationPermissions.CasesChat, cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<ChatArea>> ListAreasAsync(AccessContext access, CancellationToken cancellationToken = default)
    {
        var cases = await scopes.ListScopesAsync(access, cancellationToken);
        return [AllCases(cases), .. cases.Select(OneCase)];
    }

    /// <inheritdoc />
    public async Task<ChatArea?> ResolveAreaAsync(string? areaKey, AccessContext access, CancellationToken cancellationToken = default) =>
        await CaseAreaAsync(areaKey, access, cancellationToken) switch
        {
            { Selected: { } single } => OneCase(single),
            { } all => AllCases(all.Cases),
            null => null,
        };

    /// <inheritdoc />
    public async Task<ChatScope> ResolveScopeAsync(
        ChatArea area, AccessContext access, bool forQuestion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(area);
        if (await CaseAreaAsync(area.Key, access, cancellationToken) is not { } caseArea)
        {
            return new ChatScope([], _ => null, Unavailable: CaseChatTexts.CaseGone);
        }

        // Сведения дел и сводки перед вопросом синхронизируются с корпусом — может понадобиться модель эмбеддингов
        // (ModelUnavailableException пакет превращает в понятный отказ).
        var found = await materials.ResolveAsync(caseArea, access, sync: forQuestion, cancellationToken);
        var unavailable = caseArea.Cases.Count == 0 ? CaseChatTexts.NoCases
            : found.Items.Count == 0 ? CaseChatTexts.NoMaterials
            : null;
        return new ChatScope(
            found.CoreDocumentIds,
            fragment => Describe(found, fragment),
            CoverageText(found.Coverage),
            "Сведения дел и сводки обновляются при каждом вопросе; документы и расшифровки — после их обработки",
            unavailable);
    }

    /// <inheritdoc />
    public string? TaskPrompt(ChatArea area)
    {
        ArgumentNullException.ThrowIfNull(area);
        return CaseChatPrompt.Render(area.Description);
    }

    /// <inheritdoc />
    public ChatSuggestions Suggestions(ChatArea area)
    {
        ArgumentNullException.ThrowIfNull(area);
        return new ChatSuggestions
        {
            Grounded = CaseChatSuggestions.For(CaseChatAreas.TryParse(area.Key, out var caseId) ? caseId : null),
            Sense = CaseChatSuggestions.Sense,
        };
    }

    /// <summary>Подпись найденного фрагмента: вид, подпись (у расшифровки — с моментом записи), что открыть, дело.</summary>
    public static ChatSourceRef? Describe(ChatMaterials found, RetrievedChunk fragment)
    {
        ArgumentNullException.ThrowIfNull(found);
        ArgumentNullException.ThrowIfNull(fragment);
        if (found.ByCoreId(fragment.DocumentId) is not { } material)
        {
            return null;
        }

        var inv = CultureInfo.InvariantCulture;
        var target = material.TargetId.ToString(inv);
        var startMs = material.Kind == ChatSourceKind.Transcript ? TranscriptCorpusText.FirstTimecodeMs(fragment.Text) : null;
        var (kind, href) = material.Kind switch
        {
            ChatSourceKind.Document => ("документ", "/docflow/documents/" + target),
            ChatSourceKind.Report => ("сводка/справка", "/reports/" + target),
            ChatSourceKind.Transcript => ("расшифровка",
                "/media/assets/" + target + (startMs is { } at ? "?t=" + at.ToString(inv) : string.Empty)),
            _ => ("сведения дела", "/cases/" + target),
        };
        var label = startMs is { } ms ? material.Label + " · " + TranscriptCorpusText.Timecode(ms) : material.Label;
        return new ChatSourceRef(kind, label, href, CaseChatAreas.KeyOf(material.CaseId));
    }

    /// <summary>«Дел: 3 · Чат видит: документов 3 из 4 · расшифровок 2 из 5 · сводок и справок 6 · сведений дел 1».</summary>
    public static string CoverageText(ChatCoverage coverage)
    {
        ArgumentNullException.ThrowIfNull(coverage);
        var parts = new[]
        {
            $"документов {coverage.DocumentsIndexed} из {coverage.DocumentsVisible}",
            $"расшифровок {coverage.TranscriptsIndexed} из {coverage.TranscriptsReady}",
            $"сводок и справок {coverage.Reports}",
            $"сведений дел {coverage.CaseCards}",
        };
        return (coverage.Cases > 1 ? $"Дел: {coverage.Cases} · " : string.Empty) + "Чат видит: " + string.Join(" · ", parts);
    }

    // Область в терминах дел: ключ дела — это дело, если видно; без ключа — все видимые дела; иное — нет области.
    private async Task<CaseChatArea?> CaseAreaAsync(string? areaKey, AccessContext access, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(areaKey))
        {
            return await CaseChatArea.ResolveAsync(scopes, null, access, cancellationToken);
        }

        return CaseChatAreas.TryParse(areaKey.Trim(), out var caseId)
            ? await CaseChatArea.ResolveAsync(scopes, caseId, access, cancellationToken)
            : null;
    }

    // «Все мои дела»: вопрос и диалог без грифа (это слова сотрудника), журнал — с наибольшим грифом дел области.
    private static ChatArea AllCases(IReadOnlyList<CaseChatScope> cases)
    {
        var area = new CaseChatArea(cases, null);
        return new ChatArea(
            Key: null,
            Title: string.Create(CultureInfo.InvariantCulture, $"Все мои дела ({cases.Count})"),
            Description: area.Describe(),
            Classification: 0,
            AuditClassification: area.MaxClassification,
            DivisionId: null,
            AuditRef: AuditRefAll,
            ScopeKeys: []);
    }

    // Одно дело: вопрос, диалог и журнал — с грифом дела; переписка уходит с делом.
    private static ChatArea OneCase(CaseChatScope scope)
    {
        var area = new CaseChatArea([scope], scope);
        var key = CaseChatAreas.KeyOf(scope.CaseId);
        return new ChatArea(
            Key: key,
            Title: "№ " + scope.Number + (string.IsNullOrWhiteSpace(scope.Title) ? string.Empty : " — " + scope.Title),
            Description: area.Describe(),
            Classification: scope.Classification,
            AuditClassification: scope.Classification,
            DivisionId: scope.DivisionId,
            AuditRef: "investigation:" + key,
            ScopeKeys: [key],
            Link: new ChatLink("/cases/" + scope.CaseId.ToString(CultureInfo.InvariantCulture), "Карточка дела"));
    }
}
