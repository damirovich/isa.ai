using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using Microsoft.Extensions.Logging;

namespace ISC.AI.Modules.Media.Application.Features.Suggestions;

/// <summary>
/// Автоматическое предложение связей с фигурантами (ТФ-ПЕР-09, ADR-0035): лица носителя сравниваются с действующими
/// эталонами фигурантов ТОГО ЖЕ дела, и близкие совпадения ставятся в очередь верификации кандидатами «предложено
/// системой». Подтверждает только человек, двумя подписями (ТБ-073). Поводы сверки: обработан новый носитель; в деле
/// появился эталон или основание (сверяются уже обработанные материалы дела); сотрудник нажал «Сверить сейчас».
/// </summary>
/// <remarks>
/// <para>
/// СИСТЕМА — НЕ СУБЪЕКТ С ДОПУСКОМ. Сравнение идёт под контекстом доступа, собранным из режимных полей самого дела:
/// гриф дела — потолок, подразделение дела — единственное разрешённое. Поэтому система не видит ни шаблонов выше
/// дела, ни чужих подразделений, а область — только лица нового носителя (<c>AssetIds = [носитель]</c>): это не
/// поиск по базе, а сверка одного материала с фигурантами его дела (Приложение В ТЗ, вопрос 20).
/// </para>
/// <para>
/// ОСНОВАНИЕ (ТБ-071) — основание поиска дела, которое отдаёт профиль; дело без основания профиль в выдачу не
/// включает. ПОВТОРЫ: эталон, уже предлагавшийся на этом носителе, второй раз не предлагается (переиндексация
/// пересоздаёт лица). САМОСОВПАДЕНИЕ: лицо эталона на своём же носителе кандидатом не ставится.
/// </para>
/// <para>
/// АУДИТ (ТБ-072): каждая сессия-предложение — запись «поиск» с основанием, эталоном, порогом и кандидат-листом;
/// по каждому делу — итог запуска, даже если совпадений нет; у сверки материалов дела — один итог на всё дело.
/// Субъекта нет (сверку выполняет система; кто нажал «Сверить сейчас», журнал записал командой).
/// </para>
/// <para>
/// ЖУРНАЛ СВЕРОК (<see cref="ISuggestionRunStore"/>): каждая сверка «носитель × дело» записывается с итогом, чтобы
/// карточка носителя отличала «сверено — совпадений нет» от «ещё не сверялось».
/// </para>
/// </remarks>
public sealed partial class PersonSuggester(
    ICaseScope caseScope,
    IMediaCatalog catalog,
    IFaceSearch faceSearch,
    ISearchSessionStore sessions,
    ISuggestionRunStore runs,
    IAuditWriter auditWriter,
    IFaceDetector detector,
    IFaceEmbedder embedder,
    MediaSearchOptions options,
    ILogger<PersonSuggester> logger) : IPersonSuggester
{
    /// <summary>Субъект контекста доступа системы — нечисловой: в журнал субъектом не пишется.</summary>
    public const string SystemSubject = "system:person-suggestion";

    /// <inheritdoc />
    public async Task<PersonSuggestionResult> SuggestAsync(
        int assetId, SuggestionTrigger trigger = SuggestionTrigger.Indexing, CancellationToken cancellationToken = default)
    {
        if (!options.AutoSuggestEnabled)
        {
            return PersonSuggestionResult.None;
        }

        var targets = await caseScope.ListSuggestionTargetsAsync(assetId, cancellationToken);
        if (targets.Count == 0)
        {
            return PersonSuggestionResult.None;
        }

        var threshold = options.EffectiveAutoSuggestMaxCosineDistance;
        var total = Tally.Zero;

        foreach (var target in targets)
        {
            var tally = await SuggestForTargetAsync(assetId, target, SystemAccess(target), threshold, trigger, cancellationToken);
            total += tally;

            // ТФ-ПЕР-09: «запуск и результат — в аудите» — итог по делу пишется и при нуле совпадений.
            await auditWriter.WriteAsync(
                new AuditEntry(
                    AuditAction.Search,
                    target.Classification,
                    SubjectId: null,
                    ObjectRef: Invariant($"media:suggest:run;case:{target.CaseId};asset:{assetId}"),
                    DivisionId: target.DivisionId,
                    PayloadSensitive: Invariant(
                        $"автоматическое предложение связей (ТФ-ПЕР-09): повод: {TriggerLabel(trigger)}; основание: {target.AuthorizationRef}; ")
                        + Invariant($"эталонов фигурантов: {target.References.Count}; предложений: {tally.Sessions}; кандидатов: {tally.Candidates}; ")
                        + "порог(cos-dist)=" + threshold.ToString("F4", CultureInfo.InvariantCulture)),
                cancellationToken);
        }

        LogCompleted(logger, assetId, targets.Count, total.References, total.Sessions, total.Candidates);
        return new PersonSuggestionResult(targets.Count, total.References, total.Sessions, total.Candidates, AssetsChecked: 1);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Сверяются только носители дела, которые система видит под потолком дела, уже обработанные и с лицами:
    /// необработанный сверит конвейер после индексации, у аудиозаписи лиц нет (ADR-0026), носитель выше грифа дела
    /// системе не виден — сверять его нечем. Повторы отсекаются так же, как при индексации.
    /// </remarks>
    public async Task<PersonSuggestionResult> SuggestForCaseAsync(int caseId, CancellationToken cancellationToken = default)
    {
        if (!options.AutoSuggestEnabled)
        {
            return PersonSuggestionResult.None;
        }

        var target = await caseScope.GetSuggestionTargetAsync(caseId, cancellationToken);
        if (target is null)
        {
            LogSweepSkipped(logger, caseId);
            return PersonSuggestionResult.None;
        }

        var access = SystemAccess(target);
        var threshold = options.EffectiveAutoSuggestMaxCosineDistance;
        var assetIds = await caseScope.GetAssetIdsAsync([caseId], cancellationToken);
        var total = Tally.Zero;
        var assets = 0;

        foreach (var assetId in assetIds.Order())
        {
            var asset = await catalog.GetAsync(assetId, access, cancellationToken);
            if (asset is not { IndexStatus: MediaIndexStatus.Indexed, FaceCount: > 0 })
            {
                continue;
            }

            assets++;
            total += await SuggestForTargetAsync(assetId, target, access, threshold, SuggestionTrigger.CaseSweep, cancellationToken);
        }

        // ТФ-ПЕР-09: запуск и результат — одной записью на всё дело (предложения — своими записями выше).
        await auditWriter.WriteAsync(
            new AuditEntry(
                AuditAction.Search,
                target.Classification,
                SubjectId: null,
                ObjectRef: Invariant($"media:suggest:sweep;case:{caseId}"),
                DivisionId: target.DivisionId,
                PayloadSensitive: Invariant(
                    $"сверка материалов дела с эталонами фигурантов (ТФ-ПЕР-09): основание: {target.AuthorizationRef}; ")
                    + Invariant($"эталонов фигурантов: {target.References.Count}; носителей сверено: {assets}; ")
                    + Invariant($"предложений: {total.Sessions}; кандидатов: {total.Candidates}; ")
                    + "порог(cos-dist)=" + threshold.ToString("F4", CultureInfo.InvariantCulture)),
            cancellationToken);

        LogSweepCompleted(logger, caseId, assets, total.References, total.Sessions, total.Candidates);
        return new PersonSuggestionResult(1, total.References, total.Sessions, total.Candidates, assets);
    }

    // Потолок системы — режимные поля дела (ТБ-020/070): выше дела и вне его подразделения она не видит.
    private static AccessContext SystemAccess(SuggestionTarget target) =>
        new(SystemSubject, target.Classification, [target.DivisionId]);

    /// <summary>Носитель против всех эталонов одного дела; итог — в журнал сверок (для карточки носителя).</summary>
    private async Task<Tally> SuggestForTargetAsync(
        int assetId, SuggestionTarget target, AccessContext access, double threshold, SuggestionTrigger trigger,
        CancellationToken cancellationToken)
    {
        var topK = options.AutoSuggestCandidatesPerReference;
        var tally = Tally.Zero;
        foreach (var reference in target.References)
        {
            var created = await SuggestForReferenceAsync(assetId, target, reference, access, threshold, topK, cancellationToken);
            tally += new Tally(1, created > 0 ? 1 : 0, created);
        }

        await runs.RecordAsync(
            new SuggestionRunDraft(
                assetId, target.CaseId, trigger, tally.References, tally.Sessions, tally.Candidates, threshold,
                target.Classification, target.DivisionId),
            cancellationToken);
        return tally;
    }

    /// <summary>Одно эталонное лицо против лиц носителя: сессия-предложение с кандидатами либо ничего; возвращает число кандидатов.</summary>
    private async Task<int> SuggestForReferenceAsync(
        int assetId, SuggestionTarget target, SuggestionReference reference, AccessContext access,
        double threshold, int topK, CancellationToken cancellationToken)
    {
        if (await sessions.HasSuggestionAsync(target.CaseId, reference.PersonId, reference.FaceId, assetId, cancellationToken))
        {
            return 0;
        }

        // Шаблон эталона под потолком дела: эталон выше грифа дела, непригодный по качеству или удалённый
        // регламентом закрытия (ТБ-074) не даёт шаблона — предложения по нему нет.
        var template = await catalog.GetTemplateAsync(reference.FaceId, access, cancellationToken);
        if (template is null)
        {
            LogReferenceWithoutTemplate(logger, target.CaseId, reference.PersonId, reference.FaceId);
            return 0;
        }

        var found = await faceSearch.SearchAsync(
            new FaceSearchQuery(template, topK, threshold, IncludeStale: false, AssetIds: [assetId]), access, cancellationToken);

        // Только лица ЭТОГО носителя и не само лицо эталона (эталон, взятый с этого же носителя, совпал бы с собой).
        var candidates = found.Where(c => c.AssetId == assetId && c.FaceId != reference.FaceId).ToList();
        if (candidates.Count == 0)
        {
            return 0;
        }

        var sha = Convert.ToHexStringLower(SHA256.HashData(MemoryMarshal.AsBytes<float>(template)));
        var draft = new SearchSessionDraft(
            target.CaseId,
            target.AuthorizationRef,
            SearchScopeKind.CurrentCase,
            [target.CaseId],
            sha,
            reference.FaceId,
            ProbeCropStoredFileName: null,
            topK,
            threshold,
            detector.ModelVersion,
            embedder.ModelVersion,
            options.HnswEfSearch,
            target.Classification,
            target.DivisionId,
            RequestedByUserId: null,
            SessionOrigin.SystemSuggestion,
            reference.PersonId);
        var sessionId = await sessions.CreateAsync(draft, candidates, cancellationToken);

        // ТБ-072 — fail-closed: сбой записи журнала пробрасывается (вызывающий конвейер его залогирует).
        await auditWriter.WriteAsync(
            new AuditEntry(
                AuditAction.Search,
                target.Classification,
                SubjectId: null,
                ObjectRef: Invariant($"media:suggest:{sessionId};case:{target.CaseId};person:{reference.PersonId};asset:{assetId}"),
                DivisionId: target.DivisionId,
                PayloadSensitive: BuildPayload(target, reference, assetId, sha, threshold, topK, candidates)),
            cancellationToken);

        return candidates.Count;
    }

    /// <summary>Состав записи ТБ-072 для предложения: основание, эталон, носитель, модели, параметры, кандидат-лист.</summary>
    private string BuildPayload(
        SuggestionTarget target, SuggestionReference reference, int assetId, string sha, double threshold, int topK,
        List<FaceCandidate> candidates)
    {
        var inv = CultureInfo.InvariantCulture;
        return Invariant($"предложено системой (ТФ-ПЕР-09): основание: {target.AuthorizationRef}; ")
            + Invariant($"фигурант {reference.PersonId}, эталон — лицо {reference.FaceId}; носитель {assetId}; проба: sha256={sha}; ")
            + $"модели: детектор {detector.ModelVersion}; векторизатор {embedder.ModelVersion}; "
            + Invariant($"параметры: topK={topK}; порог(cos-dist)=") + threshold.ToString("F4", inv) + "; "
            + Invariant($"кандидаты ({candidates.Count}): ")
            + string.Join("; ", candidates.Select((c, i) =>
                Invariant($"{i + 1}:{c.FaceId}:{c.AssetId}:{(c.FrameIndex is { } f ? f.ToString(inv) : "-")}:") + c.Similarity.ToString("F4", inv)))
            + "; подтверждение — только людьми, двумя подписями (ТБ-073)";
    }

    private static string Invariant(FormattableString value) => FormattableString.Invariant(value);

    private static string TriggerLabel(SuggestionTrigger trigger) => trigger switch
    {
        SuggestionTrigger.Indexing => "обработан носитель",
        SuggestionTrigger.CaseSweep => "в деле появился эталон или основание",
        SuggestionTrigger.Manual => "сотрудник запросил сверку",
        _ => "неизвестен",
    };

    /// <summary>Счётчики сверки: сравнений «эталон × носитель», созданных предложений и кандидатов.</summary>
    private readonly record struct Tally(int References, int Sessions, int Candidates)
    {
        public static Tally Zero => default;

        public static Tally operator +(Tally left, Tally right) =>
            new(left.References + right.References, left.Sessions + right.Sessions, left.Candidates + right.Candidates);
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Предложения связей по носителю {AssetId}: дел {Cases}, эталонов {References}, предложений {Sessions}, кандидатов {Candidates}.")]
    private static partial void LogCompleted(ILogger logger, int assetId, int cases, int references, int sessions, int candidates);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Сверка материалов дела {CaseId} с эталонами: носителей {Assets}, сравнений {References}, предложений {Sessions}, кандидатов {Candidates}.")]
    private static partial void LogSweepCompleted(ILogger logger, int caseId, int assets, int references, int sessions, int candidates);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Сверка материалов дела {CaseId} не проводится: дело закрыто, нет основания поиска или эталонов фигурантов.")]
    private static partial void LogSweepSkipped(ILogger logger, int caseId);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Эталон фигуранта {PersonId} дела {CaseId} (лицо {FaceId}) без шаблона под потолком дела — пропущен.")]
    private static partial void LogReferenceWithoutTemplate(ILogger logger, int caseId, int personId, int faceId);
}
