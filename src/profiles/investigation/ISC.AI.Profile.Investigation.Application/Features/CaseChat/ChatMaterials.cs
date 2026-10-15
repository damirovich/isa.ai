using System.Globalization;
using ISC.AI.Abstractions.Retrieval;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Domain.Services;
using ISC.AI.Modules.Media.Domain.Services;
using ISC.AI.Profile.Investigation.Domain.Services;

namespace ISC.AI.Profile.Investigation.Application.Features.CaseChat;

/// <summary>Материал области чата: документ корпуса ядра и что он такое в деле.</summary>
/// <param name="CoreDocumentId">Документ корпуса ядра.</param>
/// <param name="Kind">Вид материала.</param>
/// <param name="TargetId">Что открыть: документ документооборота, сводку, носитель или дело.</param>
/// <param name="CaseId">Дело.</param>
/// <param name="Label">Подпись источника.</param>
public sealed record ChatMaterial(int CoreDocumentId, ChatSourceKind Kind, int TargetId, int CaseId, string Label);

/// <summary>Готовность материалов области для строки «что видит чат».</summary>
/// <param name="Cases">Дел в области.</param>
/// <param name="DocumentsVisible">Документов дел, видимых сотруднику.</param>
/// <param name="DocumentsIndexed">Из них с текстом в корпусе.</param>
/// <param name="TranscriptsReady">Готовых непустых расшифровок записей дел.</param>
/// <param name="TranscriptsIndexed">Из них в корпусе.</param>
/// <param name="Reports">Действующих сводок и справок в корпусе.</param>
/// <param name="CaseCards">«Сведений дела» в корпусе.</param>
public sealed record ChatCoverage(
    int Cases, int DocumentsVisible, int DocumentsIndexed, int TranscriptsReady, int TranscriptsIndexed, int Reports, int CaseCards);

/// <summary>Материалы области: документы корпуса для извлечения и готовность по видам.</summary>
/// <param name="Items">Материалы.</param>
/// <param name="Coverage">Готовность.</param>
public sealed record ChatMaterials(IReadOnlyList<ChatMaterial> Items, ChatCoverage Coverage)
{
    /// <summary>Пустая область.</summary>
    public static ChatMaterials Empty { get; } = new([], new ChatCoverage(0, 0, 0, 0, 0, 0, 0));

    /// <summary>Идентификаторы корпуса — область извлечения (<see cref="RetrievalFilter.DocumentIds"/>).</summary>
    public IReadOnlyList<int> CoreDocumentIds => Items.Select(i => i.CoreDocumentId).Distinct().ToList();

    /// <summary>Материал по документу корпуса (один документ может быть в нескольких делах — берётся первый).</summary>
    public ChatMaterial? ByCoreId(int coreDocumentId) => Items.FirstOrDefault(i => i.CoreDocumentId == coreDocumentId);
}

/// <summary>Область чата: одно дело или все доступные сотруднику дела.</summary>
/// <param name="Cases">Дела области (уже под решёткой).</param>
/// <param name="Single">Выбранное дело или <see langword="null"/> — все доступные.</param>
public sealed record ChatArea(IReadOnlyList<CaseChatScope> Cases, CaseChatScope? Single)
{
    /// <summary>Дела области.</summary>
    public IReadOnlyList<int> CaseIds => Cases.Select(c => c.CaseId).ToList();

    /// <summary>Наибольший гриф дел области (гриф записи журнала о вопросе).</summary>
    public short MaxClassification => Cases.Count == 0 ? (short)0 : Cases.Max(c => c.Classification);

    /// <summary>Область словами — для промпта и журнала.</summary>
    public string Describe() => Single is { } single
        ? $"дела № {single.Number}" + (string.IsNullOrWhiteSpace(single.Title) ? string.Empty : $" «{single.Title}»")
        : string.Create(CultureInfo.InvariantCulture, $"всех доступных вам дел ({Cases.Count})");

    /// <summary>Номер дела для подписи источника.</summary>
    public string NumberOf(int caseId) => Cases.FirstOrDefault(c => c.CaseId == caseId)?.Number ?? caseId.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Область для субъекта: выбранное дело, если видно (иначе <see langword="null"/>, ТБ-021), или все видимые дела.
    /// </summary>
    public static async Task<ChatArea?> ResolveAsync(ICaseChatStore chats, int? caseId, AccessContext access, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chats);
        if (caseId is { } id)
        {
            return await chats.GetScopeAsync(id, access, cancellationToken) is { } scope ? new ChatArea([scope], scope) : null;
        }

        return new ChatArea(await chats.ListScopesAsync(access, cancellationToken), null);
    }
}

/// <summary>
/// Материалы области чата (ADR-0051): документы дел из документооборота, расшифровки записей дел, сводки и справки,
/// «Сведения дела» — всё, что положено в корпус ядра и видно сотруднику.
/// </summary>
/// <remarks>
/// ИНВАРИАНТ ОБЛАСТИ (ТБ-020/021/071). Дела области — уже под решёткой дел (<see cref="ChatArea"/>). Документы — те, что
/// сотрудник видит в документообороте (вкладка «Документы»); расшифровки — носителей этих дел под решёткой пакета
/// «Медиа»; сводки и сведения дела — этих дел. Поверх области ядро ещё раз применяет floor допуска к фрагментам.
/// Материал вне видимости не входит в область ни строкой, ни счётчиком.
/// </remarks>
public sealed class ChatMaterialResolver(
    ICaseDocumentStore caseDocuments,
    IDocumentLookup documents,
    ICaseStore cases,
    ITranscriptCorpus transcripts,
    ICaseCorpusSync corpus)
{
    // Документы документооборота разрешаются партиями — как ограничивает порт (защита от неограниченного IN).
    private const int Batch = 200;

    /// <summary>Подпись документа: «№ 12-3/2026 от 01.10.2026 · Справка».</summary>
    public static string DocumentLabel(DocumentCorpusRef document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var number = string.IsNullOrWhiteSpace(document.RegNumber) ? "Документ без номера" : "№ " + document.RegNumber;
        return string.Create(CultureInfo.InvariantCulture, $"{number} от {document.RegDate:dd.MM.yyyy} · {document.TypeName}");
    }

    /// <summary>
    /// Материалы области. <paramref name="sync"/> — перед вопросом синхронизировать сведения дел и сводки с корпусом
    /// (может обратиться к модели эмбеддингов); без него — только уже положенное (для строки готовности).
    /// </summary>
    public async Task<ChatMaterials> ResolveAsync(ChatArea area, AccessContext access, bool sync, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(area);
        ArgumentNullException.ThrowIfNull(access);
        var caseIds = area.CaseIds;
        if (caseIds.Count == 0)
        {
            return ChatMaterials.Empty;
        }

        var items = new List<ChatMaterial>();

        // Документы дел — видимые в документообороте; в область — уже проиндексированные.
        var links = await caseDocuments.ListLinksAsync(caseIds, access, cancellationToken);
        var caseOfDocument = new Dictionary<int, int>();
        foreach (var link in links)
        {
            caseOfDocument.TryAdd(link.DocumentId, link.CaseId);
        }

        var documentsVisible = 0;
        var documentsIndexed = 0;
        foreach (var part in caseOfDocument.Keys.Chunk(Batch))
        {
            documentsVisible += (await documents.ResolveByIdsAsync(part, access, cancellationToken)).Count;
            foreach (var document in await documents.ResolveCorpusAsync(part, access, cancellationToken))
            {
                var caseId = caseOfDocument[document.DocumentId];
                items.Add(new ChatMaterial(document.CoreDocumentId, ChatSourceKind.Document, document.DocumentId, caseId,
                    DocumentLabel(document) + " · дело № " + area.NumberOf(caseId)));
                documentsIndexed++;
            }
        }

        // Расшифровки записей дел — под решёткой пакета «Медиа».
        var assetIds = await cases.ListMediaAssetIdsAsync(caseIds, cancellationToken);
        var transcriptScope = await transcripts.ResolveAsync(assetIds, access, cancellationToken);
        if (transcriptScope.Indexed.Count > 0)
        {
            var caseOfAsset = await cases.MapMediaAssetsAsync(transcriptScope.Indexed.Select(t => t.AssetId).ToList(), caseIds, cancellationToken);
            foreach (var transcript in transcriptScope.Indexed)
            {
                if (caseOfAsset.TryGetValue(transcript.AssetId, out var caseId))
                {
                    items.Add(new ChatMaterial(transcript.CoreDocumentId, ChatSourceKind.Transcript, transcript.AssetId, caseId,
                        string.Create(CultureInfo.InvariantCulture,
                            $"Расшифровка «{transcript.OriginalFileName}» · носитель № {transcript.AssetId} · дело № {area.NumberOf(caseId)}")));
                }
            }
        }

        // Сводки, справки и сведения дел — материалы профиля, синхронизируемые с корпусом.
        var profile = sync
            ? await corpus.SyncAsync(caseIds, cancellationToken)
            : await corpus.ListAsync(caseIds, cancellationToken);
        foreach (var entry in profile)
        {
            items.Add(new ChatMaterial(
                entry.CoreDocumentId,
                entry.Kind == CaseCorpusKind.Report ? ChatSourceKind.Report : ChatSourceKind.CaseCard,
                entry.SourceId,
                entry.CaseId,
                entry.Label));
        }

        return new ChatMaterials(items, new ChatCoverage(
            caseIds.Count,
            documentsVisible,
            documentsIndexed,
            transcriptScope.Transcribed,
            items.Count(i => i.Kind == ChatSourceKind.Transcript),
            profile.Count(e => e.Kind == CaseCorpusKind.Report),
            profile.Count(e => e.Kind == CaseCorpusKind.CaseCard)));
    }
}
