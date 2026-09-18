using System;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.Media.Application.Features.Assets;

/// <summary>
/// Ручная привязка вырезки лица носителя к фигуранту дела (ТФ-МЕД-03): создаёт КАНДИДАТА, а не подтверждение.
/// Оформляется поисковой сессией (ТО-инф-12) с пробой-лицом, <c>TopK = 1</c> и единственным кандидатом — тем же
/// лицом; статус остаётся «кандидат» и дальше проходит правило двух лиц (ТБ-073, GATE-5) через
/// <c>RecordVerificationCommand</c>, как любой результат поиска. Возвращает идентификатор кандидата.
/// </summary>
/// <remarks>
/// НЕ <see cref="IAuditableRequest"/>: запись ТБ-072 (субъект, дело, основание, хеш пробы, фигурант) пишется
/// вручную и полностью. Балл схожести НЕ вычислялся: в кандидате записано косинусное расстояние 1,0
/// (схожесть 0) как служебная отметка ручной привязки — это зафиксировано в аудите. ОГРАНИЧЕНИЕ КОНТРАКТА:
/// <see cref="ISearchSessionStore.CreateAsync"/> привязку к фигуранту при создании не принимает, поэтому
/// <see cref="PersonRef"/> кандидату не записывается — он передаётся в ответе и в аудите; эксперт указывает
/// фигуранта при своём решении (ТФ-ВЕР-03).
/// </remarks>
public sealed record LinkFaceToPersonCommand(int FaceId, int AuthorizationId, int PersonRef) : IRequest<ResponseDto<int>>
{
    /// <summary>Служебное косинусное расстояние кандидата ручной привязки: балл не вычислялся.</summary>
    public const double ManualLinkCosineDistance = 1.0;

    /// <inheritdoc cref="LinkFaceToPersonCommand" />
    public sealed class Handler(
        IMediaAdministration administration,
        IAccessContextProvider accessProvider,
        ICaseScope caseScope,
        IMediaCatalog catalog,
        IFaceDetector detector,
        IFaceEmbedder embedder,
        ISearchSessionStore sessionStore,
        IAuditWriter auditWriter,
        MediaSearchOptions options)
        : IRequestHandler<LinkFaceToPersonCommand, ResponseDto<int>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<int>> Handle(LinkFaceToPersonCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            // Fail-closed (ТБ-021): без контекста допуска GetCurrentAsync бросает.
            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            if (!await administration.CanSearchAsync(cancellationToken))
            {
                await AuditDeniedAsync(command, access, caseItem: null, "роль не даёт права поиска (ТП-004)", cancellationToken);
                return ResponseDto<int>.BadRequest("Привязка лица к фигуранту доступна ролям Следователь, Эксперт по лицам и Администратор.");
            }

            // ТБ-020/021 + сужение по делам (ТБ-071): лицо под решёткой и только из носителя дел субъекта; отказы неразличимы.
            var face = await catalog.GetFaceAsync(command.FaceId, access, cancellationToken);
            if (face is null || !await caseScope.IsAssetAccessibleAsync(face.AssetId, access, cancellationToken))
            {
                return ResponseDto<int>.NotFound("Лицо не найдено или недоступно.");
            }

            var caseId = await caseScope.GetCaseIdForAssetAsync(face.AssetId, access, cancellationToken);
            var caseItem = caseId is { } id ? await caseScope.GetCaseAsync(id, access, cancellationToken) : null;
            if (caseItem is null)
            {
                return ResponseDto<int>.NotFound("Лицо не найдено или недоступно.");
            }

            // ТБ-071: основание — только из перечня оснований ЭТОГО дела.
            var authorizations = await caseScope.ListAuthorizationsAsync(caseItem.CaseId, access, cancellationToken);
            var authorization = authorizations.FirstOrDefault(a => a.AuthorizationId == command.AuthorizationId);
            if (authorization is null)
            {
                await AuditDeniedAsync(command, access, caseItem, "основание не принадлежит делу", cancellationToken);
                return ResponseDto<int>.BadRequest("Привязка возможна только по основанию дела (ТБ-071).");
            }

            // ТФ-МЕД-03 «фигурант дела»: только из дела носителя, видимого субъекту (ТБ-020/070).
            var persons = await caseScope.ListPersonsAsync(caseItem.CaseId, access, cancellationToken);
            if (!persons.Any(p => p.PersonId == command.PersonRef))
            {
                await AuditDeniedAsync(command, access, caseItem, "фигурант не принадлежит делу носителя", cancellationToken);
                return ResponseDto<int>.BadRequest("Фигурант не принадлежит делу носителя либо недоступен (ТФ-МЕД-03).");
            }

            return await CreateCandidateAsync(command, access, face, caseItem, authorization, cancellationToken);
        }

        /// <summary>Сессия + единственный кандидат + аудит ТБ-072; идентификатор кандидата — из записанной сессии.</summary>
        private async Task<ResponseDto<int>> CreateCandidateAsync(
            LinkFaceToPersonCommand command, AccessContext access, FaceRow face, CaseScopeItem caseItem,
            CaseAuthorizationItem authorization, CancellationToken cancellationToken)
        {
            // Хеш пробы (ТБ-077): над байтами шаблона; у непригодного лица шаблона нет — над идентификатором лица.
            var template = face.QualityAcceptable ? await catalog.GetTemplateAsync(face.Id, access, cancellationToken) : null;
            var sha = Convert.ToHexStringLower(template is not null
                ? SHA256.HashData(MemoryMarshal.AsBytes<float>(template))
                : SHA256.HashData(Encoding.UTF8.GetBytes("face:" + face.Id.ToString(CultureInfo.InvariantCulture))));

            var draft = new SearchSessionDraft(
                caseItem.CaseId, authorization.Reference, SearchScopeKind.CurrentCase, [caseItem.CaseId],
                sha, face.Id, ProbeCropStoredFileName: null, TopK: 1, MaxCosineDistance: null,
                detector.ModelVersion, embedder.ModelVersion, options.HnswEfSearch,
                caseItem.Classification, caseItem.DivisionId, access.NumericSubjectId);
            var candidate = new FaceCandidate(
                face.Id, face.AssetId, face.FrameIndex, face.FrameTimestampMs, ManualLinkCosineDistance,
                caseItem.Classification, caseItem.DivisionId, embedder.ModelVersion);

            // Статус остаётся «кандидат»: RecordDecisionAsync здесь НЕ вызывается — двойная верификация впереди (GATE-5).
            var sessionId = await sessionStore.CreateAsync(draft, [candidate], cancellationToken);

            // АУДИТ ТБ-072 — fail-closed: нет записи — нет результата.
            await auditWriter.WriteAsync(
                new AuditEntry(
                    AuditAction.Search, caseItem.Classification, access.NumericSubjectId,
                    ObjectRef: $"media:search:{sessionId};case:{caseItem.CaseId};auth:{authorization.AuthorizationId};manual-link:{face.Id}",
                    DivisionId: caseItem.DivisionId,
                    PayloadSensitive: $"ручная привязка лица {face.Id} носителя {face.AssetId} к фигуранту {command.PersonRef}; "
                        + $"основание: {authorization.Reference} (id {authorization.AuthorizationId}); проба: sha256={sha}; "
                        + "балл не вычислялся (служебное расстояние 1,0); кандидат ждёт правила двух лиц (ТБ-073)"),
                cancellationToken);

            var rows = await sessionStore.ListCandidatesAsync(sessionId, access, cancellationToken);
            var candidateId = rows.Count > 0 ? rows[0].Id : throw new InvalidOperationException("Кандидат ручной привязки не записан.");
            return ResponseDto<int>.Ok(candidateId,
                $"Кандидат создан (сессия {sessionId}); фигуранта {command.PersonRef} укажите при решении эксперта (ТФ-ВЕР-03).");
        }

        /// <summary>Отклонённая попытка — тоже обращение к биометрии (ТБ-030/072): фиксируется с причиной.</summary>
        private Task AuditDeniedAsync(
            LinkFaceToPersonCommand command, AccessContext access, CaseScopeItem? caseItem, string reason, CancellationToken cancellationToken) =>
            auditWriter.WriteAsync(
                new AuditEntry(
                    AuditAction.Search,
                    Math.Max(access.MaxClassification, caseItem?.Classification ?? 0),
                    access.NumericSubjectId,
                    ObjectRef: $"media:search:denied;manual-link:{command.FaceId};auth:{command.AuthorizationId}",
                    DivisionId: caseItem?.DivisionId,
                    PayloadSensitive: $"ручная привязка отклонена: {reason}; фигурант {command.PersonRef}"),
                cancellationToken);
    }
}
