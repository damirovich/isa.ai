using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Application.Features.Verification;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Media;

/// <summary>Слепота верификатора (ТФ-ВЕР-02, ТБ-073): проекция очереди не несёт ни чужих решений, ни привязки к фигуранту.</summary>
public sealed class VerificationQueueBlindnessTests
{
    private const int Expert = 3;
    private const int Verifier = 7;

    [Fact(DisplayName = "Тип проекции очереди структурно не имеет свойств PersonRef/Decisions и не тянет список решений")]
    public void Queue_item_type_has_no_person_ref_and_no_foreign_decisions()
    {
        var names = typeof(VerificationQueueItem).GetProperties().Select(p => p.Name).ToList();
        names.ShouldNotContain("PersonRef");
        names.ShouldNotContain("Decisions");
        names.ShouldContain("OwnDecision");

        typeof(VerificationQueueItem).GetProperties()
            .Where(p => p.PropertyType != typeof(VerificationDecision))
            .ShouldAllBe(p => !typeof(IEnumerable<VerificationDecision>).IsAssignableFrom(p.PropertyType));
    }

    [Fact(DisplayName = "Проекция для верификатора: решение эксперта и фигурант не выдаются; своё решение — только своё")]
    public void Projection_hides_expert_decision_and_person_ref()
    {
        var candidate = Candidate(new VerificationDecision(Expert, VerificationStage.Expert, VerificationVerdict.Confirmed, "признаки эксперта", DateTime.UtcNow));
        var session = Session();

        var forVerifier = VerificationQueueItem.From(candidate, session, Verifier);
        forVerifier.OwnDecision.ShouldBeNull();
        forVerifier.Status.ShouldBe(CandidateStatus.PendingVerifier);
        forVerifier.ToString().ShouldNotContain("признаки эксперта");
        forVerifier.ProbeSha256.ShouldBe("abc");
        forVerifier.ProbeCropStoredFileName.ShouldBe("probe.jpg");

        var forExpert = VerificationQueueItem.From(candidate, session, Expert);
        forExpert.OwnDecision.ShouldNotBeNull();
        forExpert.OwnDecision.SubjectId.ShouldBe(Expert);
    }

    [Fact(DisplayName = "Очередь верификатора через сценарий: элементы без чужих решений, сессии вне допуска отсекаются")]
    public async Task Queue_query_returns_blind_items_and_skips_inaccessible_sessions()
    {
        var subjects = Substitute.For<ISubjectProvider>();
        subjects.GetCurrentUserIdAsync(Arg.Any<CancellationToken>()).Returns(Verifier);
        var policy = Substitute.For<IVerificationPolicy>();
        policy.CanActAsync(VerificationStage.Verifier, Verifier, Arg.Any<CancellationToken>()).Returns(true);
        var accessProvider = Substitute.For<IAccessContextProvider>();
        accessProvider.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(new AccessContext("7", 2, [1]));
        var caseScope = Substitute.For<ICaseScope>();
        caseScope.ListAccessibleCasesAsync(Arg.Any<AccessContext>(), Arg.Any<CancellationToken>())
            .Returns([new CaseScopeItem(3, "№ 1", "Дело", 2, 1)]);
        var store = Substitute.For<ISearchSessionStore>();
        var decision = new VerificationDecision(Expert, VerificationStage.Expert, VerificationVerdict.Confirmed, "признаки эксперта", DateTime.UtcNow);
        store.ListQueueAsync(VerificationStage.Verifier, Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>())
            .Returns([Candidate(decision), Candidate(decision) with { Id = 12, SessionId = 6 }]);
        store.GetAsync(5, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>()).Returns(Session());
        store.GetAsync(6, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>()).Returns((SearchSessionRow?)null);

        var handler = new ListVerificationQueueQuery.Handler(subjects, policy, accessProvider, caseScope, store);
        var response = await handler.Handle(new ListVerificationQueueQuery(VerificationStage.Verifier), CancellationToken.None);

        response.Status.ShouldBeTrue();
        response.Data!.Count.ShouldBe(1);
        response.Data[0].CandidateId.ShouldBe(11);
        response.Data[0].OwnDecision.ShouldBeNull();
    }

    private static SearchCandidateRow Candidate(params VerificationDecision[] decisions) =>
        new(Id: 11, SessionId: 5, CaseId: 3, Rank: 1, FaceId: 100, AssetId: 50, FrameIndex: null, FrameTimestampMs: null,
            CosineDistance: 0.2, CropStoredFileName: "crop.jpg", ModelVersion: "sface-1", Classification: 2, DivisionId: 1,
            Status: TwoPersonRule.Resolve(decisions), PersonRef: 77, Decisions: decisions);

    private static SearchSessionRow Session() =>
        new(Id: 5, CaseId: 3, AuthorizationRef: "Постановление", Scope: SearchScopeKind.CurrentCase, CaseIds: [3],
            ProbeSha256: "abc", ProbeFaceId: null, ProbeCropStoredFileName: "probe.jpg", TopK: 20, MaxCosineDistance: null,
            DetectorVersion: "yunet-1", EmbedderVersion: "sface-1", Classification: 2, DivisionId: 1, RequestedByUserId: 1,
            CreatedAt: DateTime.UtcNow, CandidateCount: 1);
}
