using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Abstractions.Storage;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using Mediator;
using Microsoft.Extensions.Logging;

namespace ISC.AI.Modules.Media.Application.Features.Search;

/// <summary>
/// Поиск по лицу в контексте дела и основания (ТФ-ПЛ-01..06, ТБ-071): проба — изображение, лицо уже
/// проиндексированного носителя (ТФ-ПЛ-03) либо несколько лиц-эталонов фигуранта (ТФ-ПЕР-03, ADR-0046); область —
/// текущее дело, выбранные или все доступные дела (ТФ-ПЛ-05); выдача — кандидат-лист, записанный поисковой сессией
/// (ТО-инф-12). По нескольким эталонам кандидаты сводятся по лицу: лучшее совпадение, эталон и число совпавших
/// эталонов; сами эталоны в кандидаты не попадают.
/// </summary>
/// <remarks>
/// НЕ <see cref="IAuditableRequest"/>: состав записи ТБ-072 (субъект, дело, основание, хеш и копия пробы,
/// область, версии моделей, параметры, полный кандидат-лист) сквозное поведение дать не может — запись
/// пишется здесь вручную и ПОЛНОСТЬЮ, в т.ч. для отклонённых и упавших попыток. Вектор пробы в базу
/// не пишется (ТБ-074): сессия хранит только хеш; сама проба в базу шаблонов не попадает. Ширину выдачи оператор
/// задаёт в границах эксплуатанта (ТН-008), порог — в процентах не ниже нижней границы Администратора (ТФ-ПЛ-08,
/// ADR-0054): без порога поиска нет. Тексты — только «кандидат» (ТЭ-005). <c>MinSimilarityPercent</c> — порог
/// «схожесть не ниже, %» с ползунка оператора; не задан — рабочий порог Администратора, ниже нижней границы —
/// поднимается до неё на сервере.
/// </remarks>
public sealed record SearchByFaceQuery(
    int CaseId,
    int AuthorizationId,
    SearchScopeKind Scope = SearchScopeKind.CurrentCase,
    IReadOnlyList<int>? SelectedCaseIds = null,
    byte[]? ProbeImage = null,
    int? ProbeFaceId = null,
    int? ProbeFaceIndex = null,
    int? TopK = null,
    int? MinSimilarityPercent = null,
    bool IncludeClosedCases = false,
    IReadOnlyList<int>? ProbeFaceIds = null)
    : IRequest<ResponseDto<FaceSearchResult>>
{
    /// <summary>Наибольшее число эталонов в одном поиске (ADR-0046): N запросов к индексу лиц в пределах ТН-006.</summary>
    public const int MaxProbeFaces = 10;

    /// <inheritdoc cref="SearchByFaceQuery" />
    public sealed class Handler(
        IMediaAdministration administration,
        IAccessContextProvider accessProvider,
        ICaseScope caseScope,
        IMediaCatalog catalog,
        IFaceDetector detector,
        IFaceQualityAssessor qualityAssessor,
        IFaceEmbedder embedder,
        IImageTools imageTools,
        IFaceSearch faceSearch,
        ISearchSessionStore sessionStore,
        IFileStorage fileStorage,
        IAuditWriter auditWriter,
        MediaSearchOptions options,
        IMediaSettingsStore settingsStore,
        ILogger<Handler> logger)
        : IRequestHandler<SearchByFaceQuery, ResponseDto<FaceSearchResult>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<FaceSearchResult>> Handle(
            SearchByFaceQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            // Fail-closed (ТБ-021): без контекста допуска GetCurrentAsync бросает — ни поиска, ни выдачи.
            var access = await accessProvider.GetCurrentAsync(cancellationToken);

            if (!await administration.CanSearchAsync(cancellationToken))
            {
                await AuditDeniedAsync(query, access, caseItem: null, "роль не даёт права поиска (ТП-004)", cancellationToken);
                return ResponseDto<FaceSearchResult>.BadRequest("Поиск по лицу доступен ролям Следователь, Эксперт по лицам и Администратор.");
            }

            // ТБ-071: поиск только в контексте дела; недоступное дело неотличимо от несуществующего (ТБ-020).
            var caseItem = await caseScope.GetCaseAsync(query.CaseId, access, cancellationToken);
            if (caseItem is null)
            {
                await AuditDeniedAsync(query, access, caseItem: null, "дело не найдено или недоступно", cancellationToken);
                return ResponseDto<FaceSearchResult>.NotFound("Дело не найдено или недоступно.");
            }

            try
            {
                return await ExecuteAsync(query, access, caseItem, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Попытка была — фиксируем не-отменяемой записью (данные могли быть уже извлечены).
                await AuditFailureAsync(query, access, caseItem, "операция отменена", CancellationToken.None);
                throw;
            }
            catch (Exception exception)
            {
                // Fail-closed: сбой записи аудита здесь не глотается — он пробрасывается вместо исходной ошибки.
                SearchByFaceLog.Failed(logger, exception, query.CaseId, query.AuthorizationId);
                await AuditFailureAsync(query, access, caseItem, exception.Message, cancellationToken);
                throw;
            }
        }

        private async Task<ResponseDto<FaceSearchResult>> ExecuteAsync(
            SearchByFaceQuery query, AccessContext access, CaseScopeItem caseItem, CancellationToken cancellationToken)
        {
            // (в) Основание (ТБ-071): только из перечня оснований ЭТОГО дела — чужое или вымышленное не проходит.
            var authorizations = await caseScope.ListAuthorizationsAsync(caseItem.CaseId, access, cancellationToken);
            var authorization = authorizations.FirstOrDefault(a => a.AuthorizationId == query.AuthorizationId);
            if (authorization is null)
            {
                await AuditDeniedAsync(query, access, caseItem, "основание не принадлежит делу", cancellationToken);
                return ResponseDto<FaceSearchResult>.BadRequest("Поиск возможен только по основанию дела (ТБ-071).");
            }

            // (г) Область (ТФ-ПЛ-05): всегда пересечение с делами, доступными субъекту по решётке и роли.
            var caseIds = await ResolveScopeAsync(query, access, caseItem, cancellationToken);
            if (caseIds.Count == 0)
            {
                await AuditDeniedAsync(query, access, caseItem, "среди выбранных дел нет доступных", cancellationToken);
                return ResponseDto<FaceSearchResult>.BadRequest("Среди выбранных дел нет доступных субъекту.");
            }

            var assetIds = await caseScope.GetAssetIdsAsync(caseIds, cancellationToken);

            // (д) Проба: шаблон уже проиндексированного лица, новое изображение (тот же конвейер, что при индексации)
            // либо несколько лиц-эталонов (ADR-0046) — эталоны без шаблона пропускаются с причиной.
            var (probes, skipped, failure) = await ResolveProbesAsync(query, access, caseItem.CaseId, cancellationToken);
            if (probes is null)
            {
                await AuditDeniedAsync(query, access, caseItem, failure!.StatusMessage, cancellationToken);
                return failure;
            }

            var probe = probes[0];
            var multiProbe = probes.Count > 1;

            try
            {
                // (е) Параметры в границах: ТН-008 (ширина) и ТФ-ПЛ-08 (порог). Порог — всегда: не задан — рабочий порог
                // Администратора, ниже нижней границы — граница (проверка на сервере, не только ползунком); перевод в
                // расстояние — шкалой и не дальше предела эксплуатанта (ADR-0054).
                var topK = options.ClampTopK(query.TopK);
                var settings = await settingsStore.GetAsync(cancellationToken);
                var threshold = new SearchThreshold(
                    query.MinSimilarityPercent, settings.EffectivePercent(query.MinSimilarityPercent), settings.MinThresholdPercent);
                double? maxDistance = options.MaxCosineDistanceFor(threshold.Percent);

                // (ж) Поиск: решётка — на стороне БД (ТБ-020/021), область — поверх неё, не вместо. По эталонам — по
                // каждому, со сводкой кандидатов по лицу (ADR-0046).
                var candidates = query.ProbeFaceIds is { } referenceFaceIds
                    ? await SearchByReferencesAsync(probes, referenceFaceIds, topK, maxDistance!.Value, assetIds, access, cancellationToken)
                    : await faceSearch.SearchAsync(
                        new FaceSearchQuery(probe.Template, topK, maxDistance, IncludeStale: false, assetIds),
                        access, cancellationToken);
                var probeSha = multiProbe ? CombinedSha(probes) : probe.Sha256;

                // (з) Сессия (ТО-инф-12): хеш пробы, параметры, версии моделей, кандидат-лист. Вектор — нет (ТБ-074).
                var draft = new SearchSessionDraft(
                    caseItem.CaseId,
                    authorization.Reference,
                    query.Scope,
                    caseIds,
                    probeSha,
                    query.ProbeFaceIds is null ? query.ProbeFaceId : probe.FaceId,
                    probe.CropStoredFileName,
                    topK,
                    maxDistance,
                    detector.ModelVersion,
                    embedder.ModelVersion,
                    options.HnswEfSearch,
                    caseItem.Classification,
                    caseItem.DivisionId,
                    access.NumericSubjectId,
                    ProbeFaceIds: multiProbe ? probes.Select(p => p.FaceId!.Value).ToList() : null);
                var sessionId = await sessionStore.CreateAsync(draft, candidates, cancellationToken);

                // (и) АУДИТ ТБ-072 — полный состав; сбой записи пробрасывается (fail-closed: нет записи — нет выдачи).
                await auditWriter.WriteAsync(
                    new AuditEntry(
                        AuditAction.Search,
                        caseItem.Classification,
                        access.NumericSubjectId,
                        ObjectRef: $"media:search:{sessionId};case:{caseItem.CaseId};auth:{authorization.AuthorizationId}",
                        DivisionId: caseItem.DivisionId,
                        PayloadSensitive: BuildAuditPayload(query, authorization, caseIds, probes, skipped, topK, threshold, maxDistance, candidates)),
                    cancellationToken);

                // (к) Кандидаты — как записаны (с идентификаторами для верификации).
                var rows = await sessionStore.ListCandidatesAsync(sessionId, access, cancellationToken);
                return ResponseDto<FaceSearchResult>.Ok(new FaceSearchResult(
                    sessionId,
                    probeSha,
                    probe.CropStoredFileName,
                    probe.DetectedFaces,
                    rows,
                    detector.ModelVersion,
                    embedder.ModelVersion,
                    topK,
                    maxDistance,
                    caseIds,
                    multiProbe ? probes.Select(p => p.FaceId!.Value).ToList() : null,
                    skipped));
            }
            catch (Exception)
            {
                // Сбой после сохранения вырезки пробы: сессии нет — файл стал бы сиротой. Снимаем его
                // (только свою вырезку изображения; вырезка лица-пробы принадлежит носителю и не трогается).
                await DeleteOwnProbeCropAsync(query, probe, caseItem.CaseId);
                throw;
            }
        }

        /// <summary>Снять вырезку пробы-изображения, если сессия так и не была создана (иначе файл-сирота под грифом дела).</summary>
        private async Task DeleteOwnProbeCropAsync(
            SearchByFaceQuery query, ProbeResolution probe, int caseId)
        {
            if (query.ProbeImage is null || probe.CropStoredFileName is null)
            {
                return;
            }

            try
            {
                await fileStorage.DeleteAsync(
                    probe.CropStoredFileName, MediaFileCategories.Probes, caseId.ToString(CultureInfo.InvariantCulture), CancellationToken.None);
            }
            catch (IOException exception)
            {
                SearchByFaceLog.ProbeCropNotDeleted(logger, exception, caseId, probe.CropStoredFileName);
            }
        }

        /// <summary>Дела области поиска: только доступные субъекту; пусто — область не содержит доступных дел.</summary>
        /// <summary>
        /// Область поиска (ТФ-ПЛ-05): текущее дело / выбранные / все доступные — и всегда только дела,
        /// доступные субъекту по решётке и роли.
        /// </summary>
        /// <remarks>
        /// ЗАКРЫТЫЕ ДЕЛА по умолчанию в область НЕ входят: в работе следователя они — шум, а материалов в
        /// них за годы больше, чем в текущих. Но исключать их совсем нельзя: тот же человек попадается по
        /// новому делу, и его прежние материалы — самое ценное, что может дать поиск. Поэтому включение
        /// закрытых дел — ОСОЗНАННОЕ действие оператора (<see cref="IncludeClosedCases"/>), и оно попадает
        /// в журнал вместе с областью (ТБ-072): «расширение области фиксируется в аудите».
        ///
        /// ТЕКУЩЕЕ ДЕЛО в область входит всегда, даже закрытое: субъект работает именно в нём, основание
        /// поиска — его, и прятать от него материалы дела, которое он сам открыл, значит сделать поиск
        /// непредсказуемым.
        /// </remarks>
        private async Task<IReadOnlyList<int>> ResolveScopeAsync(
            SearchByFaceQuery query, AccessContext access, CaseScopeItem caseItem, CancellationToken cancellationToken)
        {
            switch (query.Scope)
            {
                case SearchScopeKind.CurrentCase:
                    return [caseItem.CaseId];

                case SearchScopeKind.SelectedCases:
                {
                    var accessible = (await caseScope.ListAccessibleCasesAsync(access, cancellationToken))
                        .Where(c => Included(c, query, caseItem))
                        .Select(c => c.CaseId)
                        .ToHashSet();
                    return (query.SelectedCaseIds ?? []).Distinct().Where(accessible.Contains).ToList();
                }

                case SearchScopeKind.AllAccessibleCases:
                    // Взаимный поиск ОН+ОУ (ТБ-071, ТФ-ПЛ-05, ADR-0044): все дела в пределах допуска — floor ядра без
                    // сужения по роли. Дела ОТМ отсекает допуск по подразделениям (ТС-014), закрытые — Included.
                    return (await caseScope.ListCasesWithinClearanceAsync(access, cancellationToken))
                        .Where(c => Included(c, query, caseItem))
                        .Select(c => c.CaseId)
                        .Distinct()
                        .ToList();

                default:
                    return [];
            }
        }

        // Закрытое дело берётся в область, только если оператор его включил — либо это само текущее дело.
        private static bool Included(CaseScopeItem item, SearchByFaceQuery query, CaseScopeItem currentCase) =>
            !item.IsClosed || query.IncludeClosedCases || item.CaseId == currentCase.CaseId;

        /// <summary>
        /// Проба: шаблон лица носителя (ТФ-ПЛ-03; хеш — над байтами вектора; вырезка НЕ сохраняется и в сессию
        /// НЕ записывается — вырезка лица принадлежит носителю, интерфейс берёт её по <c>ProbeFaceId</c>, а на
        /// столбце вырезки пробы стоит уникальный индекс) либо изображение (детекция → выбор лица → качество
        /// ТО-мат-07 → шаблон; вырезка пробы сохраняется в категорию проб под подкаталогом = идентификатор
        /// ДЕЛА — та же конвенция, что у резолвера раздачи в слое данных: сессии ещё нет, дело известно).
        /// Отказ — в failure.
        /// </summary>
        private async Task<(ProbeResolution? Probe, ResponseDto<FaceSearchResult>? Failure)> ResolveProbeAsync(
            SearchByFaceQuery query, AccessContext access, int caseId, CancellationToken cancellationToken)
        {
            if (query.ProbeFaceId is { } probeFaceId)
            {
                return await ResolveFaceProbeAsync(probeFaceId, access, cancellationToken);
            }

            return await ResolveImageProbeAsync(query, caseId, cancellationToken);
        }

        /// <summary>
        /// Пробы поиска: одна (изображение или лицо носителя) либо эталоны фигуранта (ADR-0046). Эталон без шаблона —
        /// непригоден по качеству или шаблон удалён регламентом — пропускается с причиной; недоступное лицо — отказ
        /// всего поиска (ТБ-020/021: среди эталонов не может быть чужой биометрии); пригодных нет — отказ с причинами.
        /// </summary>
        private async Task<(IReadOnlyList<ProbeResolution>? Probes, IReadOnlyList<string> Skipped, ResponseDto<FaceSearchResult>? Failure)> ResolveProbesAsync(
            SearchByFaceQuery query, AccessContext access, int caseId, CancellationToken cancellationToken)
        {
            if (query.ProbeFaceIds is not { } faceIds)
            {
                var (single, failure) = await ResolveProbeAsync(query, access, caseId, cancellationToken);
                return single is null ? (null, [], failure) : ([single], [], null);
            }

            var probes = new List<ProbeResolution>(faceIds.Count);
            var skipped = new List<string>();
            foreach (var faceId in faceIds)
            {
                var (probe, failure) = await ResolveFaceProbeAsync(faceId, access, cancellationToken);
                if (probe is not null)
                {
                    probes.Add(probe);
                    continue;
                }

                if (failure!.StatusCode == ResponseStatusCode.NotFound)
                {
                    return (null, [], failure);
                }

                skipped.Add($"эталон — лицо № {faceId.ToString(CultureInfo.InvariantCulture)}: {failure.StatusMessage}");
            }

            return probes.Count == 0
                ? (null, skipped, ResponseDto<FaceSearchResult>.BadRequest("Ни один эталон не пригоден для поиска: " + string.Join("; ", skipped)))
                : (probes, skipped, null);
        }

        /// <summary>
        /// Поиск по эталонам фигуранта (ADR-0046 п. 2): по каждой пробе — ближайшие соседи в той же области, с тем же
        /// порогом и шириной; сами эталоны (<paramref name="referenceFaceIds"/>) в кандидаты не попадают — ширина
        /// каждого запроса увеличена на их число. Выдачи объединяются, отбираются лучшие по известному расстоянию, и
        /// для отобранных расстояние до КАЖДОГО эталона пересчитывается по шаблону кандидата: эталон мог не выдать
        /// кандидата не потому, что тот далёк, а потому, что выдачу заняли другие лица (в видео — десятки кадров
        /// одного человека). Итог по кандидату: наименьшее расстояние, эталон, по которому оно получено (при равенстве
        /// — первый по порядку), и число эталонов в пределах порога <paramref name="maxDistance"/>.
        /// </summary>
        /// <remarks>Шаблоны кандидатов читаются под тем же допуском и только в память — в выдачу и в базу не идут (ТБ-074).</remarks>
        private async Task<IReadOnlyList<FaceCandidate>> SearchByReferencesAsync(
            IReadOnlyList<ProbeResolution> probes,
            IReadOnlyList<int> referenceFaceIds,
            int topK,
            double maxDistance,
            IReadOnlyCollection<int> assetIds,
            AccessContext access,
            CancellationToken cancellationToken)
        {
            var references = referenceFaceIds.ToHashSet();
            var found = new Dictionary<int, FaceCandidate>();
            var distances = new Dictionary<int, Dictionary<int, double>>();
            foreach (var probe in probes)
            {
                var results = await faceSearch.SearchAsync(
                    new FaceSearchQuery(probe.Template, topK + references.Count, maxDistance, IncludeStale: false, assetIds),
                    access, cancellationToken);
                foreach (var candidate in results.Where(c => !references.Contains(c.FaceId)))
                {
                    found.TryAdd(candidate.FaceId, candidate);
                    if (!distances.TryGetValue(candidate.FaceId, out var byProbe))
                    {
                        byProbe = [];
                        distances[candidate.FaceId] = byProbe;
                    }

                    byProbe[probe.FaceId!.Value] = candidate.CosineDistance;
                }
            }

            var selected = found.Keys
                .OrderBy(faceId => distances[faceId].Values.Min())
                .ThenBy(faceId => faceId)
                .Take(topK)
                .ToList();

            // Один пригодный эталон — обычная выдача без сводных полей (сводить не с чем).
            if (probes.Count == 1)
            {
                return selected.Select(faceId => found[faceId] with { ProbeFaceId = null, ProbeMatchCount = null }).ToList();
            }

            foreach (var faceId in selected)
            {
                var template = await catalog.GetTemplateAsync(faceId, access, cancellationToken);
                foreach (var probe in probes)
                {
                    if (template is { Length: > 0 } && template.Length == probe.Template.Length)
                    {
                        distances[faceId][probe.FaceId!.Value] = CosineDistance(probe.Template, template);
                    }
                }
            }

            var order = probes.Select(p => p.FaceId!.Value).ToList();
            return selected
                .Select(faceId =>
                {
                    var byProbe = distances[faceId];
                    var bestProbe = order.Where(byProbe.ContainsKey).MinBy(id => byProbe[id]);
                    return found[faceId] with
                    {
                        CosineDistance = byProbe[bestProbe],
                        ProbeFaceId = bestProbe,
                        ProbeMatchCount = byProbe.Values.Count(distance => distance <= maxDistance),
                    };
                })
                .OrderBy(c => c.CosineDistance)
                .ThenByDescending(c => c.ProbeMatchCount ?? 0)
                .ThenBy(c => c.FaceId)
                .ToList();
        }

        // Косинусное расстояние (1 − cos) — та же метрика, что у индекса лиц (ADR-0019); нулевой вектор — «далеко».
        private static double CosineDistance(float[] a, float[] b)
        {
            double dot = 0, normA = 0, normB = 0;
            for (var i = 0; i < a.Length; i++)
            {
                dot += (double)a[i] * b[i];
                normA += (double)a[i] * a[i];
                normB += (double)b[i] * b[i];
            }

            return normA == 0 || normB == 0 ? 1 : 1 - (dot / (Math.Sqrt(normA) * Math.Sqrt(normB)));
        }

        // Хеш пробы поиска по эталонам: SHA-256 от перечня хешей шаблонов через «;» в порядке поиска (ADR-0046 п. 3).
        private static string CombinedSha(IReadOnlyList<ProbeResolution> probes) =>
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(";", probes.Select(p => p.Sha256)))));

        /// <summary>Проба — лицо уже проиндексированного носителя (ТФ-ПЛ-03) или эталон фигуранта (ADR-0046).</summary>
        private async Task<(ProbeResolution? Probe, ResponseDto<FaceSearchResult>? Failure)> ResolveFaceProbeAsync(
            int probeFaceId, AccessContext access, CancellationToken cancellationToken)
        {
            // Fail-closed (ТБ-020/021): лицо под решёткой; затем — сужение по делам субъекта (ТБ-071, ДОК-13 §248
            // «сотрудник, ищущий вне своего дела»): биометрия носителя чужого дела пробой быть не может.
            // Оба отказа наружу неразличимы («не найдено или недоступно»).
            var face = await catalog.GetFaceAsync(probeFaceId, access, cancellationToken);
            if (face is null || !await caseScope.IsAssetAccessibleAsync(face.AssetId, access, cancellationToken))
            {
                return (null, ResponseDto<FaceSearchResult>.NotFound("Лицо-проба не найдено или недоступно."));
            }

            // ТО-мат-07: лицо есть, но шаблон не строился (непригодно) — это НЕ отказ по допуску, а отказ по качеству.
            var template = face.QualityAcceptable ? await catalog.GetTemplateAsync(probeFaceId, access, cancellationToken) : null;
            if (template is null)
            {
                // «Шаблона нет» — внешне один признак, а причин две: лицо непригодно по качеству (шаблон
                // не строился, ТО-мат-07) ЛИБО шаблон был и удалён регламентом по закрытию дела (ТБ-074,
                // ADR-0024). Различаем по делу носителя, а не по догадке: объяснять регламент «плохим
                // качеством» значит отправить оператора искать дефект распознавания там, где сработало
                // правило хранения.
                var closedByRegulation = face.QualityAcceptable
                    && !await caseScope.IsBiometricIndexingAllowedAsync(face.AssetId, cancellationToken);

                return (null, ResponseDto<FaceSearchResult>.BadRequest(
                    closedByRegulation
                        ? "Поиск по этому лицу невозможен: дело закрыто, шаблон удалён регламентом (ТБ-074)."
                        : $"Лицо-проба непригодно для сравнения: {face.QualityReason ?? "шаблон не построен"} (ТО-мат-07)."));
            }

            var sha = Convert.ToHexStringLower(SHA256.HashData(MemoryMarshal.AsBytes<float>(template)));
            return (new ProbeResolution(
                template, sha, CropStoredFileName: null, DetectedFaces: 0,
                $"лицо {probeFaceId.ToString(CultureInfo.InvariantCulture)} носителя {face.AssetId.ToString(CultureInfo.InvariantCulture)}",
                probeFaceId), null);
        }

        /// <summary>Проба — изображение: детекция, выбор лица, качество (ТО-мат-07), шаблон, вырезка пробы.</summary>
        private async Task<(ProbeResolution? Probe, ResponseDto<FaceSearchResult>? Failure)> ResolveImageProbeAsync(
            SearchByFaceQuery query, int caseId, CancellationToken cancellationToken)
        {
            var image = query.ProbeImage
                ?? throw new InvalidOperationException("Проба не задана: ожидалось изображение либо лицо носителя.");

            var faces = await detector.DetectAsync(image, cancellationToken);
            if (faces.Count == 0)
            {
                return (null, ResponseDto<FaceSearchResult>.BadRequest("На пробном изображении лицо не найдено."));
            }

            int index;
            if (query.ProbeFaceIndex is { } requested)
            {
                if (requested < 0 || requested >= faces.Count)
                {
                    return (null, ResponseDto<FaceSearchResult>.BadRequest(
                        $"На пробном изображении нет лица с номером {requested.ToString(CultureInfo.InvariantCulture)} (найдено {faces.Count.ToString(CultureInfo.InvariantCulture)})."));
                }

                index = requested;
            }
            else
            {
                index = 0;
                for (var i = 1; i < faces.Count; i++)
                {
                    if (faces[i].Box.Area > faces[index].Box.Area)
                    {
                        index = i;
                    }
                }
            }

            var selected = faces[index];
            var size = imageTools.ReadSize(image);
            var quality = qualityAssessor.Assess(selected, size.Width, size.Height);
            if (!quality.Acceptable)
            {
                return (null, ResponseDto<FaceSearchResult>.BadRequest(
                    $"Пробное изображение непригодно: {quality.Reason ?? "низкое качество"} (ТО-мат-07)."));
            }

            var probeTemplate = await embedder.EmbedAsync(image, selected, cancellationToken);
            var imageSha = Convert.ToHexStringLower(SHA256.HashData(image));

            // Вырезка пробы — для показа пары «проба ↔ кандидат» (ТФ-ВЕР-01); подкаталог — ДЕЛО: идентификатор
            // сессии до её создания неизвестен, а резолвер раздачи (Media.Data) ищет вырезку пробы по делу сессии.
            var crop = imageTools.CropJpeg(image, selected.Box);
            string cropName;
            using (var cropStream = new MemoryStream(crop))
            {
                cropName = await fileStorage.SaveAsync(
                    cropStream, ".jpg", MediaFileCategories.Probes, caseId.ToString(CultureInfo.InvariantCulture), cancellationToken);
            }

            return (new ProbeResolution(
                probeTemplate, imageSha, cropName, faces.Count,
                $"изображение; лиц на пробе {faces.Count.ToString(CultureInfo.InvariantCulture)}, выбрано #{index.ToString(CultureInfo.InvariantCulture)} "
                + $"(балл детектора {selected.Score.ToString("F3", CultureInfo.InvariantCulture)}, качество {quality.Score.ToString("F3", CultureInfo.InvariantCulture)})"), null);
        }

        /// <summary>Состав записи ТБ-072: основание, область, проба (хеш + копия), модели, параметры, полный кандидат-лист.</summary>
        private string BuildAuditPayload(
            SearchByFaceQuery query,
            CaseAuthorizationItem authorization,
            IReadOnlyList<int> caseIds,
            IReadOnlyList<ProbeResolution> probes,
            IReadOnlyList<string> skipped,
            int topK,
            SearchThreshold threshold,
            double? maxDistance,
            IReadOnlyList<FaceCandidate> candidates)
        {
            var inv = CultureInfo.InvariantCulture;
            var expanded = caseIds.Any(id => id != query.CaseId);
            var sb = new StringBuilder();
            sb.Append("основание: ").Append(authorization.Reference)
              .Append(" (id ").Append(authorization.AuthorizationId.ToString(inv)).AppendLine(")");
            sb.Append("область: ").Append(query.Scope)
              .Append("; дела: ").Append(string.Join(",", caseIds.Select(id => id.ToString(inv))))
              .Append("; расширение сверх текущего дела: ").Append(expanded ? "да" : "нет")
              // ТБ-072: включение ЗАКРЫТЫХ дел — расширение области, и оно должно быть видно в журнале
              // отдельно: по закрытым делам ищут по иному поводу, чем по своей текущей работе.
              .Append("; закрытые дела: ").AppendLine(query.IncludeClosedCases ? "включены оператором" : "не включены");
            if (query.ProbeFaceIds is null)
            {
                sb.Append("проба: sha256=").Append(probes[0].Sha256).Append("; ").AppendLine(probes[0].Description);
            }
            else
            {
                // ADR-0046: все эталоны поиска с хешами шаблонов и пропущенные — с причиной.
                sb.Append("проба: эталоны фигуранта (").Append(probes.Count.ToString(inv)).Append("): ")
                  .AppendJoin("; ", probes.Select(p => $"{p.Description} sha256={p.Sha256}")).AppendLine();
                if (skipped.Count > 0)
                {
                    sb.Append("пропущены: ").AppendJoin("; ", skipped).AppendLine();
                }
            }

            if (query.ProbeImage is { } image)
            {
                if (image.LongLength <= options.ProbeCopyMaxBytes)
                {
                    sb.Append("копия пробы (base64, ").Append(image.LongLength.ToString(inv)).Append(" байт): ")
                      .AppendLine(Convert.ToBase64String(image));
                }
                else
                {
                    SearchByFaceLog.ProbeCopySkipped(logger, query.CaseId, image.LongLength, options.ProbeCopyMaxBytes);
                    sb.Append("копия не сохранена: размер ").Append(image.LongLength.ToString(inv))
                      .Append(" байт превышает предел ").Append(options.ProbeCopyMaxBytes.ToString(inv)).AppendLine(" байт");
                }
            }

            sb.Append("модели: детектор ").Append(detector.ModelVersion)
              .Append("; векторизатор ").AppendLine(embedder.ModelVersion);
            sb.Append("параметры: topK=").Append(topK.ToString(inv))
              // ТФ-ПЛ-08, ТБ-072: порог в процентах шкалы, запрошенный — если его подняли до нижней границы.
              .Append("; порог=").Append(threshold.Percent.ToString(inv)).Append(" %")
              .Append(threshold.Raised ? $" (запрошено {threshold.Requested!.Value.ToString(inv)} %, поднято до нижней границы)" : string.Empty)
              .Append(threshold.Requested is null ? " (рабочий)" : string.Empty)
              .Append("; нижняя граница=").Append(threshold.MinPercent.ToString(inv)).Append(" %")
              .Append("; шкала: ").Append(options.Scale.Describe())
              .Append("; порог(cos-dist)=").Append(maxDistance?.ToString("F4", inv) ?? "нет")
              .Append("; ef_search=").AppendLine(options.HnswEfSearch.ToString(inv));

            sb.Append("кандидаты (").Append(candidates.Count.ToString(inv)).Append("): ");
            sb.AppendJoin("; ", candidates.Select((c, i) =>
                $"{(i + 1).ToString(inv)}:{c.FaceId.ToString(inv)}:{c.AssetId.ToString(inv)}:{c.FrameIndex?.ToString(inv) ?? "-"}:{c.Similarity.ToString("F4", inv)}"
                + (c.ProbeFaceId is { } best ? $":эталон {best.ToString(inv)}, совпало {(c.ProbeMatchCount ?? 1).ToString(inv)}" : string.Empty)));

            return sb.ToString();
        }

        /// <summary>Отклонённая попытка поиска — тоже обращение (ТБ-030/072): фиксируется с причиной, без выдачи.</summary>
        private async Task AuditDeniedAsync(
            SearchByFaceQuery query, AccessContext access, CaseScopeItem? caseItem, string reason, CancellationToken cancellationToken)
        {
            SearchByFaceLog.Denied(logger, query.CaseId, query.AuthorizationId, reason);
            await auditWriter.WriteAsync(
                new AuditEntry(
                    AuditAction.Search,
                    Math.Max(access.MaxClassification, caseItem?.Classification ?? 0),
                    access.NumericSubjectId,
                    ObjectRef: $"media:search:denied;case:{query.CaseId};auth:{query.AuthorizationId}",
                    DivisionId: caseItem?.DivisionId,
                    PayloadSensitive: $"поиск отклонён: {reason}; область: {query.Scope}"),
                cancellationToken);
        }

        /// <summary>Сбой после проверок — фиксируется как попытка поиска с текстом ошибки (fail-closed, ТБ-072).</summary>
        private Task AuditFailureAsync(
            SearchByFaceQuery query, AccessContext access, CaseScopeItem caseItem, string error, CancellationToken cancellationToken) =>
            auditWriter.WriteAsync(
                new AuditEntry(
                    AuditAction.Search,
                    caseItem.Classification,
                    access.NumericSubjectId,
                    ObjectRef: $"media:search:failed;case:{caseItem.CaseId};auth:{query.AuthorizationId}",
                    DivisionId: caseItem.DivisionId,
                    PayloadSensitive: $"поиск не выполнен: {error}; область: {query.Scope}"),
                cancellationToken);

        /// <summary>Порог поиска (ТФ-ПЛ-08): запрошенный оператором (может не быть), действующий и нижняя граница — для журнала.</summary>
        private sealed record SearchThreshold(int? Requested, int Percent, int MinPercent)
        {
            /// <summary>Запрошенный порог ниже границы — сервер поднял его до границы.</summary>
            public bool Raised => Requested is { } requested && requested < Percent;
        }

        /// <summary>Разрешённая проба: вектор (только в памяти, ТБ-074), хеш, вырезка, число лиц, описание для аудита.</summary>
        private sealed record ProbeResolution(
            float[] Template, string Sha256, string? CropStoredFileName, int DetectedFaces, string Description, int? FaceId = null);
    }
}
