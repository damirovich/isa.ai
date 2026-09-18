using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Application;
using ISC.AI.Modules.Media.Application.Features.Assets;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Media;

/// <summary>
/// Ручная привязка лица к фигуранту (ТФ-МЕД-03): создаётся КАНДИДАТ (не подтверждение), только по основанию
/// дела (ТБ-071), только из носителя дел субъекта, с аудитом ТБ-072; решения не записываются (GATE-5 впереди).
/// </summary>
public sealed class LinkFaceToPersonCommandTests
{
    private static readonly float[] Template = new float[128];

    private readonly IMediaAdministration _administration = Substitute.For<IMediaAdministration>();
    private readonly IAccessContextProvider _accessProvider = Substitute.For<IAccessContextProvider>();
    private readonly ICaseScope _caseScope = Substitute.For<ICaseScope>();
    private readonly IMediaCatalog _catalog = Substitute.For<IMediaCatalog>();
    private readonly IFaceDetector _detector = Substitute.For<IFaceDetector>();
    private readonly IFaceEmbedder _embedder = Substitute.For<IFaceEmbedder>();
    private readonly ISearchSessionStore _sessions = Substitute.For<ISearchSessionStore>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();

    public LinkFaceToPersonCommandTests()
    {
        _administration.CanSearchAsync(Arg.Any<CancellationToken>()).Returns(true);
        _accessProvider.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(new AccessContext("7", 2, [1]));
        _catalog.GetFaceAsync(5, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>()).Returns(Face(5, 50));
        _catalog.GetTemplateAsync(5, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>()).Returns(Template);
        _caseScope.IsAssetAccessibleAsync(50, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>()).Returns(true);
        _caseScope.GetCaseIdForAssetAsync(50, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>()).Returns(3);
        _caseScope.GetCaseAsync(3, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>())
            .Returns(new CaseScopeItem(3, "№ 1", "Дело", Classification: 2, DivisionId: 1));
        _caseScope.ListAuthorizationsAsync(3, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>())
            .Returns([new CaseAuthorizationItem(9, "Постановление № 5")]);
        _caseScope.ListPersonsAsync(3, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>())
            .Returns([new CasePersonItem(77, "Фигурант 1")]);
        _detector.ModelVersion.Returns("yunet-1");
        _embedder.ModelVersion.Returns("sface-1");
        _sessions.CreateAsync(Arg.Any<SearchSessionDraft>(), Arg.Any<IReadOnlyList<FaceCandidate>>(), Arg.Any<CancellationToken>()).Returns(42);
        _sessions.ListCandidatesAsync(42, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>())
            .Returns([new SearchCandidateRow(11, 42, 3, 1, 5, 50, null, null, 1.0, "c.jpg", "sface-1", 2, 1, CandidateStatus.Candidate, null, [])]);
    }

    [Fact(DisplayName = "Без основания дела → BadRequest (ТБ-071), сессия не создаётся, попытка аудируется")]
    public async Task Without_authorization_is_bad_request()
    {
        var response = await HandleAsync(new LinkFaceToPersonCommand(5, AuthorizationId: 99, PersonRef: 77));

        response.StatusCode.ShouldBe(ResponseStatusCode.BadRequest);
        response.StatusMessage.ShouldContain("ТБ-071");
        await _sessions.DidNotReceive().CreateAsync(Arg.Any<SearchSessionDraft>(), Arg.Any<IReadOnlyList<FaceCandidate>>(), Arg.Any<CancellationToken>());
        await _audit.Received(1).WriteAsync(
            Arg.Is<AuditEntry>(e => e.Action == AuditAction.Search && e.ObjectRef!.Contains("denied") && e.ObjectRef.Contains("manual-link:5")),
            Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Лицо носителя чужого дела → NotFound (неразличимо), сессия не создаётся")]
    public async Task Foreign_asset_is_not_found()
    {
        _caseScope.IsAssetAccessibleAsync(50, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>()).Returns(false);

        var response = await HandleAsync(new LinkFaceToPersonCommand(5, 9, 77));

        response.StatusCode.ShouldBe(ResponseStatusCode.NotFound);
        await _sessions.DidNotReceive().CreateAsync(Arg.Any<SearchSessionDraft>(), Arg.Any<IReadOnlyList<FaceCandidate>>(), Arg.Any<CancellationToken>());
        await _caseScope.DidNotReceive().ListAuthorizationsAsync(Arg.Any<int>(), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Фигурант чужого дела → BadRequest, аудит отказа, сессия не создаётся")]
    public async Task Foreign_person_is_bad_request()
    {
        var response = await HandleAsync(new LinkFaceToPersonCommand(5, 9, PersonRef: 88));

        response.StatusCode.ShouldBe(ResponseStatusCode.BadRequest);
        response.StatusMessage.ShouldContain("ТФ-МЕД-03");
        await _sessions.DidNotReceive().CreateAsync(Arg.Any<SearchSessionDraft>(), Arg.Any<IReadOnlyList<FaceCandidate>>(), Arg.Any<CancellationToken>());
        await _audit.Received(1).WriteAsync(Arg.Is<AuditEntry>(e => e.ObjectRef!.Contains("denied")), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Без права поиска → BadRequest до обращения к лицу")]
    public async Task Without_search_right_is_bad_request()
    {
        _administration.CanSearchAsync(Arg.Any<CancellationToken>()).Returns(false);

        var response = await HandleAsync(new LinkFaceToPersonCommand(5, 9, 77));

        response.StatusCode.ShouldBe(ResponseStatusCode.BadRequest);
        await _catalog.DidNotReceive().GetFaceAsync(Arg.Any<int>(), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Успех: сессия TopK=1 с пробой-лицом без вырезки, один кандидат со служебным баллом, решений нет, аудит Search")]
    public async Task Success_creates_single_candidate_without_decision()
    {
        var response = await HandleAsync(new LinkFaceToPersonCommand(5, 9, 77));

        response.Status.ShouldBeTrue();
        response.Data.ShouldBe(11);
        response.StatusMessage.ShouldContain("77");

        var sha = Convert.ToHexStringLower(SHA256.HashData(MemoryMarshal.AsBytes<float>(Template)));
        await _sessions.Received(1).CreateAsync(
            Arg.Is<SearchSessionDraft>(d => d.CaseId == 3 && d.Scope == SearchScopeKind.CurrentCase && d.ProbeFaceId == 5
                && d.ProbeCropStoredFileName == null && d.ProbeSha256 == sha && d.TopK == 1 && d.MaxCosineDistance == null
                && d.AuthorizationRef == "Постановление № 5" && d.Classification == 2 && d.DivisionId == 1 && d.RequestedByUserId == 7),
            Arg.Is<IReadOnlyList<FaceCandidate>>(c => c.Count == 1 && c[0].FaceId == 5 && c[0].AssetId == 50
                && c[0].CosineDistance == LinkFaceToPersonCommand.ManualLinkCosineDistance && c[0].ModelVersion == "sface-1"),
            Arg.Any<CancellationToken>());

        // Статус остаётся «кандидат»: никаких решений — правило двух лиц впереди (GATE-5).
        await _sessions.DidNotReceive().RecordDecisionAsync(
            Arg.Any<int>(), Arg.Any<VerificationDecision>(), Arg.Any<CandidateStatus>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());

        await _audit.Received(1).WriteAsync(
            Arg.Is<AuditEntry>(e => e.Action == AuditAction.Search && e.SubjectId == 7 && e.Classification == 2 && e.DivisionId == 1
                && e.ObjectRef == "media:search:42;case:3;auth:9;manual-link:5"
                && e.PayloadSensitive!.Contains("фигуранту 77") && e.PayloadSensitive.Contains("балл не вычислялся")),
            Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Валидатор: лицо, основание и фигурант обязательны")]
    public void Validator_requires_face_authorization_and_person()
    {
        var validator = new LinkFaceToPersonValidator();
        validator.Validate(new LinkFaceToPersonCommand(5, 9, 77)).IsValid.ShouldBeTrue();
        validator.Validate(new LinkFaceToPersonCommand(0, 9, 77)).IsValid.ShouldBeFalse();
        validator.Validate(new LinkFaceToPersonCommand(5, 0, 77)).IsValid.ShouldBeFalse();
        validator.Validate(new LinkFaceToPersonCommand(5, 9, 0)).IsValid.ShouldBeFalse();
    }

    private static FaceRow Face(int id, int assetId) =>
        new(id, assetId, null, null, 0, 0, 10, 10, 0.9f, 0.8f, QualityAcceptable: true, null, "c.jpg", null, 2, 1);

    private async Task<ResponseDto<int>> HandleAsync(LinkFaceToPersonCommand command)
    {
        var handler = new LinkFaceToPersonCommand.Handler(
            _administration, _accessProvider, _caseScope, _catalog, _detector, _embedder, _sessions, _audit, new MediaSearchOptions());
        return await handler.Handle(command, CancellationToken.None);
    }
}
