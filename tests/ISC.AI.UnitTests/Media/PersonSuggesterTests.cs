using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Application;
using ISC.AI.Modules.Media.Application.Features.Suggestions;
using ISC.AI.Modules.Media.Application.Features.Verification;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Media;

/// <summary>
/// Автоматическое предложение связей с фигурантами (ТФ-ПЕР-09) без БД: сравнение идёт только с лицами нового
/// носителя и под потолком самого дела; эталон не совпадает сам с собой; повтор не ставится; кандидат — «предложено
/// системой» без субъекта; каждое предложение и итог по делу — в журнале; функция выключается настройкой.
/// </summary>
public sealed class PersonSuggesterTests
{
    private const int AssetId = 77;
    private const int CaseId = 5;
    private const int PersonId = 9;
    private const int ReferenceFaceId = 300;

    private static readonly float[] Template = [0.1f, 0.2f, 0.3f];
    private static readonly int[] ExpectedFaces = [501, 502];
    private static readonly int[] OnlyAsset = [AssetId];
    private static readonly int[] CaseDivision = [3];

    private readonly ICaseScope _caseScope = Substitute.For<ICaseScope>();
    private readonly IMediaCatalog _catalog = Substitute.For<IMediaCatalog>();
    private readonly IFaceSearch _faceSearch = Substitute.For<IFaceSearch>();
    private readonly ISearchSessionStore _sessions = Substitute.For<ISearchSessionStore>();
    private readonly ISuggestionRunStore _runs = Substitute.For<ISuggestionRunStore>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IFaceDetector _detector = Substitute.For<IFaceDetector>();
    private readonly IFaceEmbedder _embedder = Substitute.For<IFaceEmbedder>();
    private readonly List<AuditEntry> _entries = [];

    public PersonSuggesterTests()
    {
        _detector.ModelVersion.Returns("yunet-1");
        _embedder.ModelVersion.Returns("sface-1");
        _caseScope.ListSuggestionTargetsAsync(AssetId, Arg.Any<CancellationToken>())
            .Returns([new SuggestionTarget(CaseId, 2, 3, "Задание № З-17/26", [new SuggestionReference(PersonId, ReferenceFaceId)])]);
        _catalog.GetTemplateAsync(ReferenceFaceId, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>()).Returns(Template);
        _sessions.CreateAsync(Arg.Any<SearchSessionDraft>(), Arg.Any<IReadOnlyList<FaceCandidate>>(), Arg.Any<CancellationToken>())
            .Returns(41);
        _audit.WriteAsync(Arg.Do<AuditEntry>(_entries.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
    }

    [Fact(DisplayName = "Совпадения на новом носителе — сессия «предложено системой» с фигурантом, без субъекта; эталон и лица других носителей не попадают")]
    public async Task Creates_system_suggestion_with_own_asset_faces_only()
    {
        _faceSearch.SearchAsync(Arg.Any<FaceSearchQuery>(), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>())
            .Returns([
                Candidate(ReferenceFaceId, AssetId, 0.0),   // эталон, взятый с этого же носителя, — сам с собой
                Candidate(501, AssetId, 0.21),
                Candidate(502, AssetId, 0.34),
                Candidate(900, 12, 0.10),                  // чужой носитель — защита от области шире заданной
            ]);

        var result = await Suggester().SuggestAsync(AssetId);

        result.ShouldBe(new PersonSuggestionResult(1, 1, 1, 2, AssetsChecked: 1));
        await _sessions.Received(1).CreateAsync(
            Arg.Is<SearchSessionDraft>(d =>
                d.Origin == SessionOrigin.SystemSuggestion
                && d.SuggestedPersonRef == PersonId
                && d.ProbeFaceId == ReferenceFaceId
                && d.RequestedByUserId == null
                && d.CaseId == CaseId
                && d.AuthorizationRef == "Задание № З-17/26"
                && d.Classification == 2 && d.DivisionId == 3),
            Arg.Is<IReadOnlyList<FaceCandidate>>(c => c.Select(x => x.FaceId).SequenceEqual(ExpectedFaces)),
            Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Сравнение — только с лицами нового носителя и под потолком дела (гриф и подразделение дела), с порогом предложений")]
    public async Task Search_is_limited_to_asset_and_case_ceiling()
    {
        _faceSearch.SearchAsync(Arg.Any<FaceSearchQuery>(), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>()).Returns([]);
        var options = new MediaSearchOptions(AutoSuggestMaxCosineDistance: 0.4, AutoSuggestCandidatesPerReference: 3);

        await Suggester(options).SuggestAsync(AssetId);

        await _faceSearch.Received(1).SearchAsync(
            Arg.Is<FaceSearchQuery>(q =>
                q.AssetIds != null && q.AssetIds.SequenceEqual(OnlyAsset)
                && q.TopK == 3 && q.MaxCosineDistance == 0.4 && !q.IncludeStale),
            Arg.Is<AccessContext>(a =>
                a.MaxClassification == 2 && a.AllowedDivisions.SequenceEqual(CaseDivision) && a.NumericSubjectId == null),
            Arg.Any<CancellationToken>());
        await _catalog.Received(1).GetTemplateAsync(
            ReferenceFaceId, Arg.Is<AccessContext>(a => a.MaxClassification == 2), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Аудит: предложение и итог по делу пишутся без субъекта, с грифом дела; в итоге — основание и число предложений")]
    public async Task Writes_session_and_run_audit()
    {
        _faceSearch.SearchAsync(Arg.Any<FaceSearchQuery>(), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>())
            .Returns([Candidate(501, AssetId, 0.2)]);

        await Suggester().SuggestAsync(AssetId);

        _entries.Count.ShouldBe(2);
        _entries.ShouldAllBe(e => e.SubjectId == null && e.Classification == 2 && e.DivisionId == 3 && e.Action == AuditAction.Search);
        _entries[0].ObjectRef.ShouldBe($"media:suggest:41;case:{CaseId};person:{PersonId};asset:{AssetId}");
        _entries[0].PayloadSensitive.ShouldNotBeNull().ShouldContain("предложено системой");
        _entries[1].ObjectRef.ShouldBe($"media:suggest:run;case:{CaseId};asset:{AssetId}");
        _entries[1].PayloadSensitive.ShouldNotBeNull().ShouldContain("предложений: 1");
        _entries[1].PayloadSensitive!.ShouldContain("Задание № З-17/26");
    }

    [Fact(DisplayName = "Нет совпадений — сессии нет, но итог запуска по делу в журнале есть (ТФ-ПЕР-09: «запуск и результат»)")]
    public async Task No_matches_still_audits_the_run()
    {
        _faceSearch.SearchAsync(Arg.Any<FaceSearchQuery>(), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>()).Returns([]);

        var result = await Suggester().SuggestAsync(AssetId);

        result.SessionsCreated.ShouldBe(0);
        await _sessions.DidNotReceiveWithAnyArgs().CreateAsync(default!, default!, default);
        _entries.ShouldHaveSingleItem().PayloadSensitive.ShouldNotBeNull().ShouldContain("предложений: 0");
    }

    [Fact(DisplayName = "Повтор (переиндексация) и эталон без шаблона под потолком дела не дают нового предложения")]
    public async Task Skips_existing_suggestions_and_references_without_template()
    {
        _sessions.HasSuggestionAsync(CaseId, PersonId, ReferenceFaceId, AssetId, Arg.Any<CancellationToken>()).Returns(true);

        (await Suggester().SuggestAsync(AssetId)).SessionsCreated.ShouldBe(0);
        await _faceSearch.DidNotReceiveWithAnyArgs().SearchAsync(default!, default!, default);

        _sessions.HasSuggestionAsync(CaseId, PersonId, ReferenceFaceId, AssetId, Arg.Any<CancellationToken>()).Returns(false);
        _catalog.GetTemplateAsync(ReferenceFaceId, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>()).Returns((float[]?)null);

        (await Suggester().SuggestAsync(AssetId)).SessionsCreated.ShouldBe(0);
        await _faceSearch.DidNotReceiveWithAnyArgs().SearchAsync(default!, default!, default);
    }

    [Fact(DisplayName = "Выключено настройкой или нет подходящих дел — ничего не делается и в журнал не пишется")]
    public async Task Disabled_or_no_targets_does_nothing()
    {
        (await Suggester(new MediaSearchOptions(AutoSuggestEnabled: false)).SuggestAsync(AssetId)).ShouldBe(PersonSuggestionResult.None);
        await _caseScope.DidNotReceiveWithAnyArgs().ListSuggestionTargetsAsync(default, default);

        _caseScope.ListSuggestionTargetsAsync(AssetId, Arg.Any<CancellationToken>()).Returns([]);
        (await Suggester().SuggestAsync(AssetId)).ShouldBe(PersonSuggestionResult.None);
        _entries.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Настройки: порог предложений не мягче общего порога и не дальше предела; «false» выключает")]
    public void Options_threshold_and_switch()
    {
        new MediaSearchOptions().EffectiveAutoSuggestMaxCosineDistance.ShouldBe(MediaSearchOptions.DefaultAutoSuggestMaxCosineDistance);
        new MediaSearchOptions(MaxCosineDistance: 0.3).EffectiveAutoSuggestMaxCosineDistance.ShouldBe(0.3);
        new MediaSearchOptions(AutoSuggestMaxCosineDistance: 0.9, MaxAllowedCosineDistance: 0.6)
            .EffectiveAutoSuggestMaxCosineDistance.ShouldBe(0.6);

        var read = MediaSearchOptions.Read(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [MediaSearchOptions.AutoSuggestEnabledKey] = "false",
            [MediaSearchOptions.AutoSuggestMaxCosineDistanceKey] = "0.95",
            [MediaSearchOptions.AutoSuggestCandidatesPerReferenceKey] = "500",
        }).Build());
        read.AutoSuggestEnabled.ShouldBeFalse();
        read.AutoSuggestMaxCosineDistance.ShouldBe(MediaSearchOptions.DefaultMaxAllowedCosineDistance);
        read.AutoSuggestCandidatesPerReference.ShouldBe(MediaSearchOptions.DefaultMaxCandidateListSize);

        MediaSearchOptions.Read(new ConfigurationBuilder().Build()).AutoSuggestEnabled.ShouldBeTrue();
    }

    [Fact(DisplayName = "Очередь: «предложено системой» видят обе стадии, фигуранта — только эксперт (слепота верификатора, ТФ-ВЕР-02)")]
    public void Queue_projection_shows_suggested_person_to_expert_only()
    {
        var session = new SearchSessionRow(
            41, CaseId, "Задание № 1", SearchScopeKind.CurrentCase, [CaseId], "sha", ReferenceFaceId, null, 5, 0.5,
            "yunet-1", "sface-1", 2, 3, null, DateTime.UtcNow, 1, SessionOrigin.SystemSuggestion, PersonId);
        var candidate = new SearchCandidateRow(
            100, 41, CaseId, 1, 501, AssetId, null, null, 0.2, null, "sface-1", 2, 3, CandidateStatus.Candidate, null, [],
            Origin: SessionOrigin.SystemSuggestion, SuggestedPersonRef: PersonId);

        var forExpert = VerificationQueueItem.From(candidate, session, 7, VerificationStage.Expert);
        var forVerifier = VerificationQueueItem.From(candidate, session, 8, VerificationStage.Verifier);
        var byDefault = VerificationQueueItem.From(candidate, session, 8);

        forExpert.Origin.ShouldBe(SessionOrigin.SystemSuggestion);
        forExpert.SuggestedPersonRef.ShouldBe(PersonId);
        forVerifier.Origin.ShouldBe(SessionOrigin.SystemSuggestion);
        forVerifier.SuggestedPersonRef.ShouldBeNull();
        byDefault.SuggestedPersonRef.ShouldBeNull();
    }

    [Fact(DisplayName = "Итог сверки «носитель × дело» записывается в журнал сверок — и с совпадениями, и без (для карточки носителя)")]
    public async Task Records_run_for_each_case()
    {
        _faceSearch.SearchAsync(Arg.Any<FaceSearchQuery>(), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>())
            .Returns([Candidate(501, AssetId, 0.2), Candidate(502, AssetId, 0.3)]);

        await Suggester().SuggestAsync(AssetId);

        await _runs.Received(1).RecordAsync(
            new SuggestionRunDraft(AssetId, CaseId, SuggestionTrigger.Indexing, 1, 1, 2, MediaSearchOptions.DefaultAutoSuggestMaxCosineDistance, 2, 3),
            Arg.Any<CancellationToken>());

        _faceSearch.SearchAsync(Arg.Any<FaceSearchQuery>(), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>()).Returns([]);
        await Suggester().SuggestAsync(AssetId, SuggestionTrigger.Manual);

        await _runs.Received(1).RecordAsync(
            Arg.Is<SuggestionRunDraft>(r => r.Trigger == SuggestionTrigger.Manual && r.ReferencesChecked == 1 && r.CandidatesCreated == 0),
            Arg.Any<CancellationToken>());
        _entries.Last().PayloadSensitive.ShouldNotBeNull().ShouldContain("сотрудник запросил сверку");
    }

    [Fact(DisplayName = "Сверка материалов дела: только обработанные носители с лицами, видимые под потолком дела; один итог на дело")]
    public async Task Case_sweep_checks_indexed_assets_and_audits_once()
    {
        _caseScope.GetSuggestionTargetAsync(CaseId, Arg.Any<CancellationToken>())
            .Returns(new SuggestionTarget(CaseId, 2, 3, "Постановление № 15", [new SuggestionReference(PersonId, ReferenceFaceId)]));
        _caseScope.GetAssetIdsAsync(Arg.Is<IReadOnlyCollection<int>>(ids => ids.SequenceEqual(SweepCase)), Arg.Any<CancellationToken>())
            .Returns([AssetId, 78, 79, 80]);
        _catalog.GetAsync(AssetId, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>()).Returns(AssetRow(AssetId, MediaIndexStatus.Indexed, 2));
        _catalog.GetAsync(78, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>()).Returns(AssetRow(78, MediaIndexStatus.Processing, 0));
        _catalog.GetAsync(79, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>()).Returns(AssetRow(79, MediaIndexStatus.Indexed, 0));
        _catalog.GetAsync(80, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>()).Returns((MediaAssetRow?)null); // выше грифа дела
        _faceSearch.SearchAsync(Arg.Any<FaceSearchQuery>(), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>())
            .Returns([Candidate(501, AssetId, 0.2)]);

        var result = await Suggester().SuggestForCaseAsync(CaseId);

        result.ShouldBe(new PersonSuggestionResult(1, 1, 1, 1, AssetsChecked: 1));
        await _catalog.Received(4).GetAsync(
            Arg.Any<int>(), Arg.Is<AccessContext>(a => a.MaxClassification == 2 && a.NumericSubjectId == null), Arg.Any<CancellationToken>());
        await _faceSearch.Received(1).SearchAsync(
            Arg.Is<FaceSearchQuery>(q => q.AssetIds != null && q.AssetIds.SequenceEqual(OnlyAsset)), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>());
        await _runs.Received(1).RecordAsync(
            Arg.Is<SuggestionRunDraft>(r => r.AssetId == AssetId && r.Trigger == SuggestionTrigger.CaseSweep && r.CandidatesCreated == 1),
            Arg.Any<CancellationToken>());

        _entries.Count.ShouldBe(2); // предложение + итог сверки дела (без итогов по каждому носителю)
        _entries[1].ObjectRef.ShouldBe($"media:suggest:sweep;case:{CaseId}");
        _entries[1].SubjectId.ShouldBeNull();
        _entries[1].PayloadSensitive.ShouldNotBeNull().ShouldContain("носителей сверено: 1");
    }

    [Fact(DisplayName = "Сверка дела: дело не готово (закрыто, без основания или эталонов) или функция выключена — ничего не делается")]
    public async Task Case_sweep_does_nothing_without_target_or_when_disabled()
    {
        _caseScope.GetSuggestionTargetAsync(CaseId, Arg.Any<CancellationToken>()).Returns((SuggestionTarget?)null);

        (await Suggester().SuggestForCaseAsync(CaseId)).ShouldBe(PersonSuggestionResult.None);
        (await Suggester(new MediaSearchOptions(AutoSuggestEnabled: false)).SuggestForCaseAsync(CaseId)).ShouldBe(PersonSuggestionResult.None);

        await _caseScope.Received(1).GetSuggestionTargetAsync(CaseId, Arg.Any<CancellationToken>());
        await _caseScope.DidNotReceiveWithAnyArgs().GetAssetIdsAsync(default!, default);
        _entries.ShouldBeEmpty();
    }

    private static readonly int[] SweepCase = [CaseId];

    private static MediaAssetRow AssetRow(int id, MediaIndexStatus status, int faces) => new(
        id, MediaKind.Image, "photo.jpg", "s.jpg", "image/jpeg", 10, null, null, null, 2, 3, 1, status, null,
        "yunet-1", "sface-1", null, DateTime.UtcNow, faces);

    private PersonSuggester Suggester(MediaSearchOptions? options = null) =>
        new(_caseScope, _catalog, _faceSearch, _sessions, _runs, _audit, _detector, _embedder, options ?? new MediaSearchOptions(),
            NullLogger<PersonSuggester>.Instance);

    private static FaceCandidate Candidate(int faceId, int assetId, double distance) =>
        new(faceId, assetId, null, null, distance, 2, 3, "sface-1");
}
