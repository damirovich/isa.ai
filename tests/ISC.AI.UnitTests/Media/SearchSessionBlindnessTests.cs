using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Application.Features.Search;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Media;

/// <summary>
/// Слепота второй стадии через страницу СЕССИИ (ТФ-ВЕР-02, ТБ-073): пока кандидат не завершён, сессия
/// показывает субъекту только его собственные решения и его собственную привязку к фигуранту; после
/// итога — всё. Иначе верификатор узнавал бы исход эксперта в обход очереди.
/// </summary>
public sealed class SearchSessionBlindnessTests
{
    private const int Expert = 3;
    private const int Verifier = 7;

    [Fact(DisplayName = "Незавершённый кандидат: верификатор не видит решение и фигуранта эксперта")]
    public void Pending_candidate_hides_foreign_decisions_and_person()
    {
        var candidate = Candidate(CandidateStatus.PendingVerifier, personRef: 42,
            Decision(Expert, VerificationStage.Expert, VerificationVerdict.Confirmed));

        var blind = GetSearchSessionQuery.Handler.Blind(candidate, Verifier);

        blind.Decisions.ShouldBeEmpty();
        blind.PersonRef.ShouldBeNull();
        blind.Status.ShouldBe(CandidateStatus.PendingVerifier);
    }

    [Fact(DisplayName = "Незавершённый кандидат: эксперт видит своё решение и свою привязку")]
    public void Pending_candidate_keeps_own_decision_and_person()
    {
        var candidate = Candidate(CandidateStatus.PendingVerifier, personRef: 42,
            Decision(Expert, VerificationStage.Expert, VerificationVerdict.Confirmed));

        var blind = GetSearchSessionQuery.Handler.Blind(candidate, Expert);

        blind.Decisions.ShouldHaveSingleItem().SubjectId.ShouldBe(Expert);
        blind.PersonRef.ShouldBe(42);
    }

    [Theory(DisplayName = "Завершённый кандидат показывается целиком любому, кому доступна сессия")]
    [InlineData(CandidateStatus.Confirmed)]
    [InlineData(CandidateStatus.Rejected)]
    [InlineData(CandidateStatus.Undetermined)]
    public void Finished_candidate_is_shown_in_full(CandidateStatus status)
    {
        var candidate = Candidate(status, personRef: 42,
            Decision(Expert, VerificationStage.Expert, VerificationVerdict.Confirmed),
            Decision(Verifier, VerificationStage.Verifier, VerificationVerdict.Confirmed));

        var blind = GetSearchSessionQuery.Handler.Blind(candidate, userId: 99);

        blind.Decisions.Count.ShouldBe(2);
        blind.PersonRef.ShouldBe(42);
    }

    [Fact(DisplayName = "Субъект без идентификатора не видит ни одного решения незавершённого кандидата")]
    public void Anonymous_subject_sees_nothing_pending()
    {
        var candidate = Candidate(CandidateStatus.Candidate, personRef: 42,
            Decision(Expert, VerificationStage.Expert, VerificationVerdict.Rejected));

        var blind = GetSearchSessionQuery.Handler.Blind(candidate, userId: null);

        blind.Decisions.ShouldBeEmpty();
        blind.PersonRef.ShouldBeNull();
    }

    [Fact(DisplayName = "Обработчик сессии применяет слепоту к каждому кандидату выдачи")]
    public async Task Handler_applies_blindness_to_rows()
    {
        var access = new AccessContext("7", 2, [1]);
        var accessProvider = Substitute.For<IAccessContextProvider>();
        accessProvider.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(access);
        var subjects = Substitute.For<ISubjectProvider>();
        subjects.GetCurrentUserIdAsync(Arg.Any<CancellationToken>()).Returns(Verifier);
        var store = Substitute.For<ISearchSessionStore>();
        store.GetAsync(5, access, Arg.Any<CancellationToken>()).Returns(Session());
        store.ListCandidatesAsync(5, access, Arg.Any<CancellationToken>()).Returns(new List<SearchCandidateRow>
        {
            Candidate(CandidateStatus.PendingVerifier, 42, Decision(Expert, VerificationStage.Expert, VerificationVerdict.Confirmed)),
            Candidate(CandidateStatus.Confirmed, 43,
                Decision(Expert, VerificationStage.Expert, VerificationVerdict.Confirmed),
                Decision(Verifier, VerificationStage.Verifier, VerificationVerdict.Confirmed)),
        });

        var response = await new GetSearchSessionQuery.Handler(accessProvider, subjects, store)
            .Handle(new GetSearchSessionQuery(5), CancellationToken.None);

        response.Status.ShouldBeTrue();
        response.Data!.Candidates[0].Decisions.ShouldBeEmpty();
        response.Data.Candidates[0].PersonRef.ShouldBeNull();
        response.Data.Candidates[1].Decisions.Count.ShouldBe(2);
        response.Data.Candidates[1].PersonRef.ShouldBe(43);
    }

    private static VerificationDecision Decision(int subject, VerificationStage stage, VerificationVerdict verdict) =>
        new(subject, stage, verdict, "признаки", DateTime.UtcNow);

    private static SearchCandidateRow Candidate(CandidateStatus status, int? personRef, params VerificationDecision[] decisions) =>
        new(11, 5, 1, 1, 100, 200, null, null, 0.2, "crop.jpg", "sface-test", 1, 1, status, personRef, decisions);

    private static SearchSessionRow Session() =>
        new(5, 1, "поручение № 1", SearchScopeKind.CurrentCase, [1], "sha", null, null, 20, null, "yunet", "sface", 1, 1, Expert, DateTime.UtcNow, 2);
}
