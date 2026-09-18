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
/// Поиск по лицу в контексте дела и основания (ТФ-ПЛ-01..06, ТБ-071): проба — изображение либо лицо уже
/// проиндексированного носителя (ТФ-ПЛ-03); область — текущее дело, выбранные или все доступные дела
/// (ТФ-ПЛ-05); выдача — кандидат-лист, записанный поисковой сессией (ТО-инф-12).
/// </summary>
/// <remarks>
/// НЕ <see cref="IAuditableRequest"/>: состав записи ТБ-072 (субъект, дело, основание, хеш и копия пробы,
/// область, версии моделей, параметры, полный кандидат-лист) сквозное поведение дать не может — запись
/// пишется здесь вручную и ПОЛНОСТЬЮ, в т.ч. для отклонённых и упавших попыток. Вектор пробы в базу
/// не пишется (ТБ-074): сессия хранит только хеш; сама проба в базу шаблонов не попадает. Порог и ширину
/// выдачи оператор задаёт в границах эксплуатанта (ТН-008, ТФ-ПЛ-06). Тексты — только «кандидат» (ТЭ-005).
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
    double? MaxCosineDistance = null)
    : IRequest<ResponseDto<FaceSearchResult>>
{
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
                return ResponseDto<FaceSearchResult>.BadRequest("Поиск по лицу доступен ролям Следователь/Эксперт по лицам.");
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

            // (д) Проба: шаблон уже проиндексированного лица либо новое изображение (тот же конвейер, что при индексации).
            var (probe, failure) = await ResolveProbeAsync(query, access, caseItem.CaseId, cancellationToken);
            if (probe is null)
            {
                await AuditDeniedAsync(query, access, caseItem, failure!.StatusMessage, cancellationToken);
                return failure;
            }

            try
            {
                // (е) Параметры в границах эксплуатанта: ТН-008 (ширина) и ТФ-ПЛ-06 (порог не ослабляется сверх предела).
                var topK = options.ClampTopK(query.TopK);
                var maxDistance = options.EffectiveMaxCosineDistance(query.MaxCosineDistance);

                // (ж) Поиск: решётка — на стороне БД (ТБ-020/021), область — поверх неё, не вместо.
                var candidates = await faceSearch.SearchAsync(
                    new FaceSearchQuery(probe.Template, topK, maxDistance, IncludeStale: false, assetIds),
                    access, cancellationToken);

                // (з) Сессия (ТО-инф-12): хеш пробы, параметры, версии моделей, кандидат-лист. Вектор — нет (ТБ-074).
                var draft = new SearchSessionDraft(
                    caseItem.CaseId,
                    authorization.Reference,
                    query.Scope,
                    caseIds,
                    probe.Sha256,
                    query.ProbeFaceId,
                    probe.CropStoredFileName,
                    topK,
                    maxDistance,
                    detector.ModelVersion,
                    embedder.ModelVersion,
                    options.HnswEfSearch,
                    caseItem.Classification,
                    caseItem.DivisionId,
                    access.NumericSubjectId);
                var sessionId = await sessionStore.CreateAsync(draft, candidates, cancellationToken);

                // (и) АУДИТ ТБ-072 — полный состав; сбой записи пробрасывается (fail-closed: нет записи — нет выдачи).
                await auditWriter.WriteAsync(
                    new AuditEntry(
                        AuditAction.Search,
                        caseItem.Classification,
                        access.NumericSubjectId,
                        ObjectRef: $"media:search:{sessionId};case:{caseItem.CaseId};auth:{authorization.AuthorizationId}",
                        DivisionId: caseItem.DivisionId,
                        PayloadSensitive: BuildAuditPayload(query, authorization, caseIds, probe, topK, maxDistance, candidates)),
                    cancellationToken);

                // (к) Кандидаты — как записаны (с идентификаторами для верификации).
                var rows = await sessionStore.ListCandidatesAsync(sessionId, access, cancellationToken);
                return ResponseDto<FaceSearchResult>.Ok(new FaceSearchResult(
                    sessionId,
                    probe.Sha256,
                    probe.CropStoredFileName,
                    probe.DetectedFaces,
                    rows,
                    detector.ModelVersion,
                    embedder.ModelVersion,
                    topK,
                    maxDistance,
                    caseIds));
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
                        .Select(c => c.CaseId).ToHashSet();
                    return (query.SelectedCaseIds ?? []).Distinct().Where(accessible.Contains).ToList();
                }

                case SearchScopeKind.AllAccessibleCases:
                    return (await caseScope.ListAccessibleCasesAsync(access, cancellationToken))
                        .Select(c => c.CaseId).Distinct().ToList();

                default:
                    return [];
            }
        }

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
                    return (null, ResponseDto<FaceSearchResult>.BadRequest(
                        $"Лицо-проба непригодно для сравнения: {face.QualityReason ?? "шаблон не построен"} (ТО-мат-07)."));
                }

                var sha = Convert.ToHexStringLower(SHA256.HashData(MemoryMarshal.AsBytes<float>(template)));
                return (new ProbeResolution(
                    template, sha, CropStoredFileName: null, DetectedFaces: 0,
                    $"лицо {probeFaceId.ToString(CultureInfo.InvariantCulture)} носителя {face.AssetId.ToString(CultureInfo.InvariantCulture)}"), null);
            }

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
            ProbeResolution probe,
            int topK,
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
              .Append("; расширение сверх текущего дела: ").AppendLine(expanded ? "да" : "нет");
            sb.Append("проба: sha256=").Append(probe.Sha256).Append("; ").AppendLine(probe.Description);

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
              .Append("; порог(cos-dist)=").Append(maxDistance?.ToString("F4", inv) ?? "нет")
              .Append("; ef_search=").AppendLine(options.HnswEfSearch.ToString(inv));

            sb.Append("кандидаты (").Append(candidates.Count.ToString(inv)).Append("): ");
            sb.AppendJoin("; ", candidates.Select((c, i) =>
                $"{(i + 1).ToString(inv)}:{c.FaceId.ToString(inv)}:{c.AssetId.ToString(inv)}:{c.FrameIndex?.ToString(inv) ?? "-"}:{c.Similarity.ToString("F4", inv)}"));

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

        /// <summary>Разрешённая проба: вектор (только в памяти, ТБ-074), хеш, вырезка, число лиц, описание для аудита.</summary>
        private sealed record ProbeResolution(
            float[] Template, string Sha256, string? CropStoredFileName, int DetectedFaces, string Description);
    }
}
