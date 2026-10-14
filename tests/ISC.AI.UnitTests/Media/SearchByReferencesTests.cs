using System;
using System.Collections.Generic;
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
using ISC.AI.Modules.Media.Application;
using ISC.AI.Modules.Media.Application.Features.Search;
using ISC.AI.Modules.Media.Application.Features.Verification;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Media;

/// <summary>
/// Поиск по всем эталонам фигуранта (ТФ-ПЕР-03, ADR-0046): одна сессия с несколькими пробами-лицами; кандидаты сведены
/// по лицу — наименьшее расстояние, эталон лучшего совпадения, число совпавших эталонов; сами эталоны в кандидаты не
/// попадают; эталон без шаблона пропускается с причиной, недоступный — отказ всего поиска; журнал ТБ-072 — полный.
/// </summary>
public sealed class SearchByReferencesTests
{
    // Эталоны — взаимно перпендикулярные векторы: расстояние между ними 1, до своего «соседа» задаётся явно.
    private static readonly float[] TemplateA = Template(0);
    private static readonly float[] TemplateB = Template(1);
    private static readonly float[] TemplateC = Template(2);
    private static readonly int[] AllThree = [5, 6, 7];

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

    public SearchByReferencesTests()
    {
        _administration.CanSearchAsync(Arg.Any<CancellationToken>()).Returns(true);
        _accessProvider.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(new AccessContext("7", 2, [1]));
        _caseScope.IsBiometricIndexingAllowedAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);
        _caseScope.GetCaseAsync(3, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>())
            .Returns(new CaseScopeItem(3, "№ 1", "Дело", Classification: 2, DivisionId: 1));
        _caseScope.ListAuthorizationsAsync(3, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>())
            .Returns([new CaseAuthorizationItem(9, "Постановление № 5")]);
        _caseScope.GetAssetIdsAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>()).Returns([50, 51, 52, 60]);
        _detector.ModelVersion.Returns("yunet-1");
        _embedder.ModelVersion.Returns("sface-1");

        // Эталоны 5, 6, 7 — с шаблонами; 8 и 11 — непригодны по качеству; 9 — вне допуска.
        Reference(5, 50, TemplateA);
        Reference(6, 51, TemplateB);
        Reference(7, 52, TemplateC);
        Reference(8, 53, template: null);
        Reference(11, 54, template: null);

        // Кандидат 100 найден всеми тремя (лучше всего — по эталону 6); эталон 6 сам находится по эталону 5.
        Finds(TemplateA, (100, 0.30), (101, 0.20), (6, 0.05));
        Finds(TemplateB, (100, 0.10), (102, 0.40));
        Finds(TemplateC, (100, 0.25));
        _sessions.CreateAsync(Arg.Any<SearchSessionDraft>(), Arg.Any<IReadOnlyList<FaceCandidate>>(), Arg.Any<CancellationToken>()).Returns(42);
    }

    [Fact(DisplayName = "ADR-0046: кандидаты сведены по лицу — лучшее расстояние, эталон, число совпавших; эталоны в кандидаты не попадают")]
    public async Task Candidates_are_aggregated_by_face()
    {
        IReadOnlyList<FaceCandidate>? stored = null;
        _sessions.CreateAsync(Arg.Any<SearchSessionDraft>(), Arg.Do<IReadOnlyList<FaceCandidate>>(c => stored = c), Arg.Any<CancellationToken>())
            .Returns(42);

        var response = await HandleAsync(Query([5, 6, 7]));

        response.Status.ShouldBeTrue();
        var candidates = stored.ShouldNotBeNull();
        candidates.Select(c => c.FaceId).ShouldBe([100, 101, 102]);
        candidates[0].CosineDistance.ShouldBe(0.10);
        candidates[0].ProbeFaceId.ShouldBe(6);
        candidates[0].ProbeMatchCount.ShouldBe(3);
        candidates[1].ProbeFaceId.ShouldBe(5);
        candidates[1].ProbeMatchCount.ShouldBe(1);
        candidates.ShouldNotContain(c => c.FaceId == 6); // сам эталон — не кандидат

        // Ширина каждого запроса — с запасом на исключаемые эталоны; порог не задан — рабочий порог системы 0,5.
        await _faceSearch.Received(3).SearchAsync(
            Arg.Is<FaceSearchQuery>(q => q.TopK == new MediaSearchOptions().ClampTopK(null) + 3 && q.MaxCosineDistance == 0.5),
            Arg.Any<AccessContext>(), Arg.Any<CancellationToken>());
        response.Data!.MaxCosineDistance.ShouldBe(0.5);

        var combined = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join(";", new[] { TemplateA, TemplateB, TemplateC }.Select(Sha)))));
        await _sessions.Received(1).CreateAsync(
            Arg.Is<SearchSessionDraft>(d => d.ProbeFaceId == 5 && d.ProbeFaceIds!.SequenceEqual(AllThree)
                && d.ProbeSha256 == combined && d.ProbeCropStoredFileName == null),
            Arg.Any<IReadOnlyList<FaceCandidate>>(), Arg.Any<CancellationToken>());
        response.Data!.ProbeFaceIds.ShouldBe([5, 6, 7]);
        response.Data.SkippedProbes.ShouldNotBeNull().ShouldBeEmpty();

        // ТБ-072: все эталоны с хешами и лучший эталон с числом совпавших у кандидата.
        await _audit.Received(1).WriteAsync(
            Arg.Is<AuditEntry>(e => e.ObjectRef == "media:search:42;case:3;auth:9"
                && e.PayloadSensitive!.Contains("эталоны фигуранта (3)")
                && e.PayloadSensitive.Contains(Sha(TemplateB))
                && e.PayloadSensitive.Contains("1:100:60:-:0.9000:эталон 6, совпало 3")),
            Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "ADR-0046: согласие эталонов — по точному расстоянию до каждого эталона, даже если эталон не выдал кандидата сам")]
    public async Task Agreement_is_recomputed_from_candidate_template()
    {
        IReadOnlyList<FaceCandidate>? stored = null;
        _sessions.CreateAsync(Arg.Any<SearchSessionDraft>(), Arg.Do<IReadOnlyList<FaceCandidate>>(c => stored = c), Arg.Any<CancellationToken>())
            .Returns(42);

        // Кандидат 103 «посередине» между эталонами 5 и 6 (расстояние ≈ 0,293 до каждого), далёк от 7; выдал его только
        // эталон 5 — у эталона 6 выдачу заняли другие лица. Кандидат 104 близок только к эталону 5 — согласия нет.
        var between = new float[128];
        between[0] = between[1] = (float)Math.Sqrt(0.5);
        var nearA = new float[128];
        nearA[0] = 0.9f;
        nearA[2] = 0.1f;
        Finds(TemplateA, (103, 0.293), (104, 0.10));
        Finds(TemplateB);
        Finds(TemplateC);
        _catalog.GetTemplateAsync(103, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>()).Returns(between);
        _catalog.GetTemplateAsync(104, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>()).Returns(nearA);

        await HandleAsync(Query([5, 6, 7]));

        var candidates = stored.ShouldNotBeNull();
        var middle = candidates.Single(c => c.FaceId == 103);
        middle.ProbeMatchCount.ShouldBe(2);
        middle.ProbeFaceId.ShouldBe(5); // при равных расстояниях — первый эталон по порядку
        middle.CosineDistance.ShouldBe(1 - Math.Sqrt(0.5), 0.0001);
        candidates.Single(c => c.FaceId == 104).ProbeMatchCount.ShouldBe(1);
    }

    [Fact(DisplayName = "ADR-0046: эталон без шаблона пропускается с причиной (на странице и в журнале), поиск идёт по остальным")]
    public async Task Unusable_reference_is_skipped_with_reason()
    {
        var response = await HandleAsync(Query([5, 8, 6]));

        response.Status.ShouldBeTrue();
        response.Data!.ProbeFaceIds.ShouldBe([5, 6]);
        response.Data.SkippedProbes.ShouldNotBeNull().ShouldHaveSingleItem().ShouldContain("лицо № 8");
        await _audit.Received(1).WriteAsync(
            Arg.Is<AuditEntry>(e => e.PayloadSensitive!.Contains("пропущены: эталон — лицо № 8")), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "ADR-0046: пригоден один эталон — обычная сессия по одному лицу, но эталоны в кандидаты всё равно не попадают")]
    public async Task Single_usable_reference_is_plain_search()
    {
        IReadOnlyList<FaceCandidate>? stored = null;
        _sessions.CreateAsync(Arg.Any<SearchSessionDraft>(), Arg.Do<IReadOnlyList<FaceCandidate>>(c => stored = c), Arg.Any<CancellationToken>())
            .Returns(42);
        Finds(TemplateA, (5, 0.0), (100, 0.30)); // эталон находит сам себя

        var response = await HandleAsync(Query([5, 8]));

        response.Data!.ProbeFaceIds.ShouldBeNull();
        await _sessions.Received(1).CreateAsync(
            Arg.Is<SearchSessionDraft>(d => d.ProbeFaceId == 5 && d.ProbeFaceIds == null && d.ProbeSha256 == Sha(TemplateA)),
            Arg.Any<IReadOnlyList<FaceCandidate>>(), Arg.Any<CancellationToken>());
        stored.ShouldNotBeNull().ShouldAllBe(c => c.ProbeFaceId == null && c.ProbeMatchCount == null);
        stored.Select(c => c.FaceId).ShouldBe([100]);
    }

    [Fact(DisplayName = "ADR-0046: эталон вне допуска — отказ всего поиска «не найдено» (ТБ-020/021); непригодны все — отказ с причинами")]
    public async Task Inaccessible_or_all_unusable_references_are_refused()
    {
        var hidden = await HandleAsync(Query([5, 9]));
        hidden.StatusCode.ShouldBe(ResponseStatusCode.NotFound);

        var unusable = await HandleAsync(Query([8, 11]));
        unusable.StatusCode.ShouldBe(ResponseStatusCode.BadRequest);
        unusable.StatusMessage.ShouldContain("Ни один эталон не пригоден");

        await _faceSearch.DidNotReceive().SearchAsync(Arg.Any<FaceSearchQuery>(), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>());
        await _sessions.DidNotReceive().CreateAsync(Arg.Any<SearchSessionDraft>(), Arg.Any<IReadOnlyList<FaceCandidate>>(), Arg.Any<CancellationToken>());
        await _audit.Received(2).WriteAsync(Arg.Is<AuditEntry>(e => e.ObjectRef!.StartsWith("media:search:denied", StringComparison.Ordinal)), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "ADR-0046: проба — ровно одна (изображение, лицо или эталоны); эталонов 2–10, без повторов; номер лица — только с изображением")]
    public void Validator_rules_for_references()
    {
        var validator = new SearchByFaceValidator();

        validator.Validate(Query([5, 6])).IsValid.ShouldBeTrue();
        validator.Validate(Query([5])).IsValid.ShouldBeFalse();
        validator.Validate(Query(Enumerable.Range(1, SearchByFaceQuery.MaxProbeFaces + 1).ToArray())).IsValid.ShouldBeFalse();
        validator.Validate(Query([5, 5])).IsValid.ShouldBeFalse();
        validator.Validate(Query([5, 6]) with { ProbeFaceId = 5 }).IsValid.ShouldBeFalse();
        validator.Validate(Query([5, 6]) with { ProbeImage = [1, 2, 3] }).IsValid.ShouldBeFalse();
        validator.Validate(Query([5, 6]) with { ProbeFaceIndex = 0 }).IsValid.ShouldBeFalse();
    }

    [Fact(DisplayName = "ADR-0046: пара в верификации — эталон лучшего совпадения и «совпало k из N»; у одиночного поиска — проба сессии")]
    public void Verification_pair_uses_best_reference()
    {
        var multi = Session(probeFaceId: 5, references: [5, 6, 7]);
        var item = VerificationQueueItem.From(Candidate(probeFaceId: 6, matches: 3), multi, userId: 7);
        item.ProbeFaceId.ShouldBe(6);
        item.ProbeMatchCount.ShouldBe(3);
        item.ProbeTotal.ShouldBe(3);

        var single = VerificationQueueItem.From(Candidate(probeFaceId: null, matches: null), Session(probeFaceId: 5, references: null), userId: 7);
        single.ProbeFaceId.ShouldBe(5);
        single.ProbeTotal.ShouldBeNull();
    }

    private static SearchByFaceQuery Query(int[] references) =>
        new(3, AuthorizationId: 9, Scope: SearchScopeKind.CurrentCase, ProbeFaceIds: references);

    private void Reference(int faceId, int assetId, float[]? template)
    {
        _catalog.GetFaceAsync(faceId, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>())
            .Returns(new FaceRow(faceId, assetId, null, null, 0, 0, 10, 10, 0.9f, template is null ? 0.1f : 0.8f, template is not null,
                template is null ? "размыто" : null, "c.jpg", null, 2, 1));
        _catalog.GetTemplateAsync(faceId, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>()).Returns(template);
        _caseScope.IsAssetAccessibleAsync(assetId, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>()).Returns(true);
    }

    private void Finds(float[] template, params (int Face, double Distance)[] found) =>
        _faceSearch.SearchAsync(Arg.Is<FaceSearchQuery>(q => ReferenceEquals(q.Probe, template)), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>())
            .Returns(found.Select(f => new FaceCandidate(f.Face, 60, null, null, f.Distance, 2, 1, "sface-1")).ToList());

    private static float[] Template(int axis)
    {
        var vector = new float[128];
        vector[axis] = 1;
        return vector;
    }

    private static string Sha(float[] template) => Convert.ToHexStringLower(SHA256.HashData(MemoryMarshal.AsBytes<float>(template)));

    private static SearchSessionRow Session(int probeFaceId, IReadOnlyList<int>? references) => new(
        42, 3, "Постановление № 5", SearchScopeKind.CurrentCase, [3], "sha", probeFaceId, null, 20, null, "yunet-1", "sface-1",
        2, 1, 7, DateTime.UtcNow, 3, ProbeFaceIds: references);

    private static SearchCandidateRow Candidate(int? probeFaceId, int? matches) => new(
        1, 42, 3, 1, 100, 60, null, null, 0.1, "c.jpg", "sface-1", 2, 1, CandidateStatus.Candidate, null, [],
        ProbeFaceId: probeFaceId, ProbeMatchCount: matches);

    private async Task<ResponseDto<FaceSearchResult>> HandleAsync(SearchByFaceQuery query) =>
        await new SearchByFaceQuery.Handler(
                _administration, _accessProvider, _caseScope, _catalog, _detector, _quality, _embedder, _imageTools,
                _faceSearch, _sessions, _files, _audit, new MediaSearchOptions(), NullLogger<SearchByFaceQuery.Handler>.Instance)
            .Handle(query, CancellationToken.None);
}
