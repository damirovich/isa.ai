using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Abstractions.Storage;
using ISC.AI.Modules.Media.Application;
using ISC.AI.Modules.Media.Application.Features.Search;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Media;

/// <summary>Поиск по лицу (ТБ-071/072/074, ТН-008, ТФ-ПЛ-06): основание, область, аудит, проба не сохраняется, границы параметров.</summary>
public sealed class SearchByFaceQueryTests
{
    private static readonly byte[] Probe = [1, 2, 3, 4, 5];

    private readonly IMediaAdministration _administration = Substitute.For<IMediaAdministration>();
    private readonly IAccessContextProvider _accessProvider = Substitute.For<IAccessContextProvider>();
    private readonly ICaseScope _caseScope = Substitute.For<ICaseScope>();
    private readonly IMediaCatalog _catalog = Substitute.For<IMediaCatalog>();
    private readonly IFaceDetector _detector = Substitute.For<IFaceDetector>();
    private readonly IFaceQualityAssessor _quality = Substitute.For<IFaceQualityAssessor>();
    private readonly IFaceEmbedder _embedder = Substitute.For<IFaceEmbedder>();
    private readonly IImageTools _imageTools = Substitute.For<IImageTools>();
    private readonly IFaceSearch _faceSearch = Substitute.For<IFaceSearch>();
    private readonly ISearchSessionStore _sessions = Substitute.For<ISearchSessionStore>();
    private readonly IFileStorage _files = Substitute.For<IFileStorage>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private MediaSearchOptions _options = new();

    public SearchByFaceQueryTests()
    {
        _administration.CanSearchAsync(Arg.Any<CancellationToken>()).Returns(true);
        _accessProvider.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(new AccessContext("7", 2, [1]));
        _caseScope.GetCaseAsync(3, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>())
            .Returns(new CaseScopeItem(3, "№ 1", "Дело", Classification: 2, DivisionId: 1));
        _caseScope.ListAuthorizationsAsync(3, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>())
            .Returns([new CaseAuthorizationItem(9, "Постановление № 5")]);
        _caseScope.GetAssetIdsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>()).Returns([50]);

        var face = new DetectedFace(new BoundingBox(10, 10, 100, 100), default, 0.95f);
        _detector.ModelVersion.Returns("yunet-1");
        _detector.DetectAsync(Arg.Any<byte[]>(), Arg.Any<CancellationToken>()).Returns([face]);
        _embedder.ModelVersion.Returns("sface-1");
        _embedder.EmbedAsync(Arg.Any<byte[]>(), Arg.Any<DetectedFace>(), Arg.Any<CancellationToken>()).Returns(new float[128]);
        _imageTools.ReadSize(Arg.Any<byte[]>()).Returns(new ImageSize(640, 480));
        _imageTools.CropJpeg(Arg.Any<byte[]>(), Arg.Any<BoundingBox>(), Arg.Any<float>(), Arg.Any<int>(), Arg.Any<int>()).Returns([9, 9]);
        _quality.Assess(Arg.Any<DetectedFace>(), Arg.Any<int>(), Arg.Any<int>()).Returns(new FaceQuality(0.9f, true, null));
        _faceSearch.SearchAsync(Arg.Any<FaceSearchQuery>(), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>())
            .Returns([new FaceCandidate(100, 50, null, null, 0.1, 2, 1, "sface-1")]);
        _sessions.CreateAsync(Arg.Any<SearchSessionDraft>(), Arg.Any<IReadOnlyList<FaceCandidate>>(), Arg.Any<CancellationToken>()).Returns(42);
        _files.SaveAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("probe.jpg");
    }

    [Fact(DisplayName = "Без основания дела → BadRequest (ТБ-071), поиск не выполняется, попытка аудируется")]
    public async Task Without_authorization_is_bad_request()
    {
        var response = await HandleAsync(new SearchByFaceQuery(3, AuthorizationId: 99, ProbeImage: Probe));

        response.StatusCode.ShouldBe(ResponseStatusCode.BadRequest);
        response.StatusMessage.ShouldContain("ТБ-071");
        await _faceSearch.DidNotReceive().SearchAsync(Arg.Any<FaceSearchQuery>(), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>());
        await _audit.Received(1).WriteAsync(
            Arg.Is<AuditEntry>(e => e.Action == AuditAction.Search && e.ObjectRef!.Contains("denied")), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Чужое/несуществующее дело → NotFound, поиск не выполняется")]
    public async Task Foreign_case_is_not_found()
    {
        var response = await HandleAsync(new SearchByFaceQuery(77, AuthorizationId: 9, ProbeImage: Probe));

        response.StatusCode.ShouldBe(ResponseStatusCode.NotFound);
        await _faceSearch.DidNotReceive().SearchAsync(Arg.Any<FaceSearchQuery>(), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Успешный поиск: проба не попадает в базу шаблонов и в категории носителей/вырезок (ТБ-074); аудит ТБ-072 полный")]
    public async Task Success_does_not_store_probe_and_audits_fully()
    {
        var response = await HandleAsync(new SearchByFaceQuery(3, AuthorizationId: 9, ProbeImage: Probe));

        response.Status.ShouldBeTrue();
        response.Data!.SessionId.ShouldBe(42);
        var sha = Convert.ToHexStringLower(SHA256.HashData(Probe));
        response.Data.ProbeSha256.ShouldBe(sha);

        // ТБ-074: обработчик вообще не зависит от хранилища носителей — пробе некуда «осесть».
        typeof(SearchByFaceQuery.Handler).GetConstructors().Single().GetParameters()
            .ShouldNotContain(p => p.ParameterType == typeof(IMediaStore));
        await _files.DidNotReceive().SaveAsync(Arg.Any<Stream>(), Arg.Any<string>(), MediaFileCategories.Originals, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _files.DidNotReceive().SaveAsync(Arg.Any<Stream>(), Arg.Any<string>(), MediaFileCategories.FaceCrops, Arg.Any<string>(), Arg.Any<CancellationToken>());
        // Вырезка пробы — в категории проб под подкаталогом ДЕЛА (конвенция резолвера раздачи Media.Data).
        await _files.Received(1).SaveAsync(Arg.Any<Stream>(), ".jpg", MediaFileCategories.Probes, "3", Arg.Any<CancellationToken>());

        // Сессия: хеш, без вектора (в черновике вектора просто нет), гриф/подразделение — дела.
        await _sessions.Received(1).CreateAsync(
            Arg.Is<SearchSessionDraft>(d => d.ProbeSha256 == sha && d.Classification == 2 && d.DivisionId == 1
                && d.AuthorizationRef == "Постановление № 5" && d.ProbeCropStoredFileName == "probe.jpg" && d.RequestedByUserId == 7),
            Arg.Any<IReadOnlyList<FaceCandidate>>(), Arg.Any<CancellationToken>());

        // ТБ-072: субъект, дело, основание, хеш и копия пробы, область, модели, параметры, кандидат-лист.
        await _audit.Received(1).WriteAsync(
            Arg.Is<AuditEntry>(e => e.Action == AuditAction.Search && e.SubjectId == 7 && e.Classification == 2 && e.DivisionId == 1
                && e.ObjectRef == "media:search:42;case:3;auth:9"
                && e.PayloadSensitive!.Contains(sha)
                && e.PayloadSensitive.Contains(Convert.ToBase64String(Probe))
                && e.PayloadSensitive.Contains("Постановление № 5")
                && e.PayloadSensitive.Contains("yunet-1") && e.PayloadSensitive.Contains("sface-1")
                && e.PayloadSensitive.Contains("1:100:50:-:0.9000")),
            Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Порог не ослабляется сверх предела эксплуатанта (ТФ-ПЛ-06); ужесточить — можно")]
    public async Task Threshold_is_capped_by_max_allowed()
    {
        await HandleAsync(new SearchByFaceQuery(3, 9, ProbeImage: Probe, MaxCosineDistance: 0.95));
        await _faceSearch.Received(1).SearchAsync(Arg.Is<FaceSearchQuery>(q => q.MaxCosineDistance == 0.8), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>());

        var response = await HandleAsync(new SearchByFaceQuery(3, 9, ProbeImage: Probe, MaxCosineDistance: 0.3));
        response.Data!.MaxCosineDistance.ShouldBe(0.3);
    }

    [Fact(DisplayName = "Ширина кандидат-листа зажимается в 5..50 (ТН-008); умолчание — 20")]
    public async Task TopK_is_clamped()
    {
        (await HandleAsync(new SearchByFaceQuery(3, 9, ProbeImage: Probe, TopK: 500))).Data!.TopK.ShouldBe(50);
        (await HandleAsync(new SearchByFaceQuery(3, 9, ProbeImage: Probe, TopK: 1))).Data!.TopK.ShouldBe(5);
        (await HandleAsync(new SearchByFaceQuery(3, 9, ProbeImage: Probe))).Data!.TopK.ShouldBe(20);
        await _faceSearch.Received(1).SearchAsync(Arg.Is<FaceSearchQuery>(q => q.TopK == 50), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Копия пробы больше предела — в аудит не кладётся, но факт и размер фиксируются")]
    public async Task Oversized_probe_copy_is_skipped_in_audit()
    {
        _options = new MediaSearchOptions(ProbeCopyMaxBytes: 2);

        await HandleAsync(new SearchByFaceQuery(3, 9, ProbeImage: Probe));

        await _audit.Received(1).WriteAsync(
            Arg.Is<AuditEntry>(e => e.PayloadSensitive!.Contains("копия не сохранена") && !e.PayloadSensitive.Contains(Convert.ToBase64String(Probe))),
            Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Область «выбранные дела» — только пересечение с доступными; вне доступных → BadRequest")]
    public async Task Selected_scope_intersects_accessible_cases()
    {
        _caseScope.ListAccessibleCasesAsync(Arg.Any<AccessContext>(), Arg.Any<CancellationToken>())
            .Returns([new CaseScopeItem(3, "№ 1", "Дело", 2, 1), new CaseScopeItem(4, "№ 2", "Дело 2", 2, 1)]);

        var ok = await HandleAsync(new SearchByFaceQuery(3, 9, SearchScopeKind.SelectedCases, SelectedCaseIds: [4, 8], ProbeImage: Probe));
        ok.Data!.CaseIds.ShouldBe([4]);

        var denied = await HandleAsync(new SearchByFaceQuery(3, 9, SearchScopeKind.SelectedCases, SelectedCaseIds: [8], ProbeImage: Probe));
        denied.StatusCode.ShouldBe(ResponseStatusCode.BadRequest);
    }

    [Fact(DisplayName = "Валидатор: ровно одна проба (изображение либо лицо), основание обязательно")]
    public void Validator_requires_exactly_one_probe()
    {
        var validator = new SearchByFaceValidator();
        validator.Validate(new SearchByFaceQuery(3, 9, ProbeImage: Probe)).IsValid.ShouldBeTrue();
        validator.Validate(new SearchByFaceQuery(3, 9, ProbeFaceId: 5)).IsValid.ShouldBeTrue();
        validator.Validate(new SearchByFaceQuery(3, 9)).IsValid.ShouldBeFalse();
        validator.Validate(new SearchByFaceQuery(3, 9, ProbeImage: Probe, ProbeFaceId: 5)).IsValid.ShouldBeFalse();
        validator.Validate(new SearchByFaceQuery(3, 0, ProbeImage: Probe)).IsValid.ShouldBeFalse();
        validator.Validate(new SearchByFaceQuery(3, 9, SearchScopeKind.SelectedCases, ProbeImage: Probe)).IsValid.ShouldBeFalse();
    }

    private async Task<ResponseDto<FaceSearchResult>> HandleAsync(SearchByFaceQuery query)
    {
        var handler = new SearchByFaceQuery.Handler(
            _administration, _accessProvider, _caseScope, _catalog, _detector, _quality, _embedder, _imageTools,
            _faceSearch, _sessions, _files, _audit, _options, NullLogger<SearchByFaceQuery.Handler>.Instance);
        return await handler.Handle(query, CancellationToken.None);
    }
}
