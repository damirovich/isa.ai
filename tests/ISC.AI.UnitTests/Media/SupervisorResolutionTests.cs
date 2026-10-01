using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Application.Features.Verification;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Media;

/// <summary>
/// Итог руководителя по расхождению (ТФ-ВЕР-02, ADR-0036) без БД: решает только по «неопределённому» кандидату и только
/// третьим лицом; «отклонён» — всегда, «подтверждён» — только вместе с положительным решением другого сотрудника
/// (ТБ-073); появление подписано подтвердившим и руководителем; отказы — в журнале; руководителю видны оба решения.
/// </summary>
public sealed class SupervisorResolutionTests
{
    private const int Expert = 3;
    private const int Verifier = 4;
    private const int Head = 9;
    private const int Person = 77;

    private readonly ISubjectProvider _subjects = Substitute.For<ISubjectProvider>();
    private readonly IVerificationPolicy _policy = Substitute.For<IVerificationPolicy>();
    private readonly IAccessContextProvider _accessProvider = Substitute.For<IAccessContextProvider>();
    private readonly ISearchSessionStore _store = Substitute.For<ISearchSessionStore>();
    private readonly ICaseScope _caseScope = Substitute.For<ICaseScope>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();

    public SupervisorResolutionTests()
    {
        _subjects.GetCurrentUserIdAsync(Arg.Any<CancellationToken>()).Returns(Head);
        _policy.CanActAsync(Arg.Any<VerificationStage>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);
        _accessProvider.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(new AccessContext("9", 2, [1]));
        _caseScope.ListPersonsAsync(3, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>())
            .Returns([new CasePersonItem(Person, "Фигурант 1"), new CasePersonItem(78, "Фигурант 2")]);
    }

    [Fact(DisplayName = "Правило: «отклонён» руководителя → отклонён; «подтверждён» → подтверждён только при положительном решении другого; иначе — неопределённо")]
    public void Rule_resolves_disagreement()
    {
        var split = Decisions(VerificationVerdict.Confirmed, VerificationVerdict.Rejected);
        TwoPersonRule.Resolve(split).ShouldBe(CandidateStatus.Undetermined);

        TwoPersonRule.Resolve([.. split, Decision(Head, VerificationStage.Supervisor, VerificationVerdict.Rejected)])
            .ShouldBe(CandidateStatus.Rejected);
        TwoPersonRule.Resolve([.. split, Decision(Head, VerificationStage.Supervisor, VerificationVerdict.Confirmed)])
            .ShouldBe(CandidateStatus.Confirmed);

        // Ни один из двоих не подтвердил — «подтверждён» руководителя один статуса не даёт (ТБ-073).
        var bothUnsure = Decisions(VerificationVerdict.Undetermined, VerificationVerdict.Rejected);
        TwoPersonRule.Resolve([.. bothUnsure, Decision(Head, VerificationStage.Supervisor, VerificationVerdict.Confirmed)])
            .ShouldBe(CandidateStatus.Undetermined);

        // Решение руководителя не трогает уже согласованный итог и не считается от эксперта/верификатора.
        var agreed = Decisions(VerificationVerdict.Confirmed, VerificationVerdict.Confirmed);
        TwoPersonRule.Resolve([.. agreed, Decision(Head, VerificationStage.Supervisor, VerificationVerdict.Rejected)])
            .ShouldBe(CandidateStatus.Confirmed);
        TwoPersonRule.Resolve([.. split, Decision(Verifier, VerificationStage.Supervisor, VerificationVerdict.Confirmed)])
            .ShouldBe(CandidateStatus.Undetermined);
    }

    [Fact(DisplayName = "Правило: руководитель — только по «неопределённому», один раз и не эксперт/верификатор; «неопределённо» ему не выбрать")]
    public void Rule_limits_who_and_when()
    {
        var split = Decisions(VerificationVerdict.Confirmed, VerificationVerdict.Rejected);

        TwoPersonRule.CanDecide(split, VerificationStage.Supervisor, Head, out _).ShouldBeTrue();
        TwoPersonRule.CanDecide(split, VerificationStage.Supervisor, Expert, out var asExpert).ShouldBeFalse();
        asExpert.ShouldNotBeNull().ShouldContain("третье лицо");
        TwoPersonRule.CanDecide([Decision(Expert, VerificationStage.Expert, VerificationVerdict.Confirmed)],
            VerificationStage.Supervisor, Head, out _).ShouldBeFalse();
        TwoPersonRule.CanDecide(Decisions(VerificationVerdict.Rejected, VerificationVerdict.Rejected),
            VerificationStage.Supervisor, Head, out _).ShouldBeFalse();
        TwoPersonRule.CanDecide([.. split, Decision(Head, VerificationStage.Supervisor, VerificationVerdict.Rejected)],
            VerificationStage.Supervisor, 10, out _).ShouldBeFalse();

        TwoPersonRule.CanSupervisorDecide(split, VerificationVerdict.Confirmed, Head, out _).ShouldBeTrue();
        TwoPersonRule.CanSupervisorDecide(split, VerificationVerdict.Rejected, Head, out _).ShouldBeTrue();
        TwoPersonRule.CanSupervisorDecide(split, VerificationVerdict.Undetermined, Head, out _).ShouldBeFalse();
        TwoPersonRule.CanSupervisorDecide(Decisions(VerificationVerdict.Undetermined, VerificationVerdict.Rejected),
            VerificationVerdict.Confirmed, Head, out var noPositive).ShouldBeFalse();
        noPositive.ShouldNotBeNull().ShouldContain("ТБ-073");

        var validator = new RecordVerificationValidator();
        validator.Validate(new RecordVerificationCommand(11, VerificationStage.Supervisor, VerificationVerdict.Undetermined, "признаки"))
            .IsValid.ShouldBeFalse();
        validator.Validate(new RecordVerificationCommand(11, VerificationStage.Supervisor, VerificationVerdict.Confirmed, "признаки", Person))
            .IsValid.ShouldBeTrue();
    }

    [Fact(DisplayName = "Руководитель подтверждает при «подтверждён» эксперта: статус «подтверждён», появление подписано экспертом и руководителем, в журнале все трое")]
    public async Task Confirm_with_expert_positive_creates_appearance()
    {
        GivenCandidate(personRef: Person, Decisions(VerificationVerdict.Confirmed, VerificationVerdict.Rejected));

        var response = await HandleAsync(new RecordVerificationCommand(11, VerificationStage.Supervisor, VerificationVerdict.Confirmed, "уши и нос совпадают"));

        response.Data.ShouldBe(CandidateStatus.Confirmed);
        await _store.Received(1).RecordDecisionAsync(
            11, Arg.Is<VerificationDecision>(d => d.Stage == VerificationStage.Supervisor && d.SubjectId == Head),
            CandidateStatus.Confirmed, null, Arg.Any<CancellationToken>());
        await _caseScope.Received(1).RecordAppearanceAsync(
            Arg.Is<ConfirmedAppearance>(a => a.PersonRef == Person && a.ExpertUserId == Expert && a.VerifierUserId == Head),
            Arg.Any<CancellationToken>());
        await _audit.Received(1).WriteAsync(
            Arg.Is<AuditEntry>(e => e.ObjectRef == "media:candidate:11:decision:Supervisor"
                && e.PayloadSensitive!.Contains("решение руководителя=9")
                && e.PayloadSensitive.Contains("эксперт=3:Confirmed")
                && e.PayloadSensitive.Contains("верификатор=4:Rejected")
                && e.PayloadSensitive.Contains(TwoPersonRule.ConfirmedMarker)),
            Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Подтвердил верификатор, а эксперт отклонил без фигуранта: руководитель указывает фигуранта, появление подписано верификатором и руководителем")]
    public async Task Confirm_with_verifier_positive_requires_person()
    {
        GivenCandidate(personRef: null, Decisions(VerificationVerdict.Rejected, VerificationVerdict.Confirmed));

        var withoutPerson = await HandleAsync(new RecordVerificationCommand(11, VerificationStage.Supervisor, VerificationVerdict.Confirmed, "признаки"));
        withoutPerson.StatusMessage.ShouldBe(RecordVerificationValidator.PersonRequiredMessage);

        var foreign = await HandleAsync(new RecordVerificationCommand(11, VerificationStage.Supervisor, VerificationVerdict.Confirmed, "признаки", PersonRef: 99));
        foreign.StatusMessage.ShouldNotBeNull().ShouldContain("ТФ-ВЕР-03");

        var response = await HandleAsync(new RecordVerificationCommand(11, VerificationStage.Supervisor, VerificationVerdict.Confirmed, "признаки", PersonRef: 78));

        response.Data.ShouldBe(CandidateStatus.Confirmed);
        await _store.Received(1).RecordDecisionAsync(11, Arg.Any<VerificationDecision>(), CandidateStatus.Confirmed, 78, Arg.Any<CancellationToken>());
        await _caseScope.Received(1).RecordAppearanceAsync(
            Arg.Is<ConfirmedAppearance>(a => a.PersonRef == 78 && a.ExpertUserId == Verifier && a.VerifierUserId == Head),
            Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Отказы руководителю аудируются: без права, не «неопределённый», сам решал, «подтверждён» без положительного решения")]
    public async Task Refusals_are_audited()
    {
        GivenCandidate(personRef: Person, Decisions(VerificationVerdict.Undetermined, VerificationVerdict.Rejected));
        var noPositive = await HandleAsync(new RecordVerificationCommand(11, VerificationStage.Supervisor, VerificationVerdict.Confirmed, "признаки"));
        noPositive.StatusMessage.ShouldNotBeNull().ShouldContain("ТБ-073");

        _subjects.GetCurrentUserIdAsync(Arg.Any<CancellationToken>()).Returns(Expert);
        (await HandleAsync(new RecordVerificationCommand(11, VerificationStage.Supervisor, VerificationVerdict.Rejected, "признаки")))
            .StatusMessage.ShouldNotBeNull().ShouldContain("третье лицо");

        _subjects.GetCurrentUserIdAsync(Arg.Any<CancellationToken>()).Returns(Head);
        GivenCandidate(personRef: Person, Decision(Expert, VerificationStage.Expert, VerificationVerdict.Confirmed));
        (await HandleAsync(new RecordVerificationCommand(11, VerificationStage.Supervisor, VerificationVerdict.Rejected, "признаки")))
            .Status.ShouldBeFalse();

        _policy.CanActAsync(VerificationStage.Supervisor, Head, Arg.Any<CancellationToken>()).Returns(false);
        (await HandleAsync(new RecordVerificationCommand(11, VerificationStage.Supervisor, VerificationVerdict.Rejected, "признаки")))
            .StatusMessage.ShouldBe(VerificationMessages.SupervisorDenied);

        await _audit.Received(4).WriteAsync(
            Arg.Is<AuditEntry>(e => e.ObjectRef == "media:candidate:11:decision-denied"), Arg.Any<CancellationToken>());
        await _store.DidNotReceive().RecordDecisionAsync(
            Arg.Any<int>(), Arg.Any<VerificationDecision>(), Arg.Any<CandidateStatus>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
        await _caseScope.DidNotReceive().RecordAppearanceAsync(Arg.Any<ConfirmedAppearance>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "«Отклонён» руководителя — статус «отклонён», появления нет")]
    public async Task Reject_resolves_to_rejected()
    {
        GivenCandidate(personRef: Person, Decisions(VerificationVerdict.Confirmed, VerificationVerdict.Undetermined));

        (await HandleAsync(new RecordVerificationCommand(11, VerificationStage.Supervisor, VerificationVerdict.Rejected, "разные уши")))
            .Data.ShouldBe(CandidateStatus.Rejected);
        await _caseScope.DidNotReceive().RecordAppearanceAsync(Arg.Any<ConfirmedAppearance>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Обзор руководителя: оба решения и привязанный фигурант — только после обоих решений и только по праву; общая проекция остаётся слепой")]
    public async Task Supervisor_review_requires_both_decisions()
    {
        GivenCandidate(personRef: Person, Decisions(VerificationVerdict.Confirmed, VerificationVerdict.Rejected));

        var review = (await ReviewAsync()).Data.ShouldNotBeNull();
        review.Decisions.Select(d => d.Stage).ShouldBe([VerificationStage.Expert, VerificationStage.Verifier]);
        review.LinkedPersonRef.ShouldBe(Person);
        new GetSupervisorReviewQuery(11).AuditSummary.ShouldBe("media:candidate:11:supervisor-review");

        // Верификатор ещё не решал — решение эксперта не выдаётся никому (иначе администратор с обоими правами обошёл бы слепоту).
        GivenCandidate(personRef: Person, Decision(Expert, VerificationStage.Expert, VerificationVerdict.Confirmed));
        (await ReviewAsync()).StatusCode.ShouldBe(ResponseStatusCode.NotFound);

        _policy.CanActAsync(VerificationStage.Supervisor, Head, Arg.Any<CancellationToken>()).Returns(false);
        (await ReviewAsync()).StatusMessage.ShouldBe(VerificationMessages.SupervisorDenied);

        // Проекция очереди руководителю даёт предложение системы, но не чужие решения и не привязку.
        var session = new SearchSessionRow(
            5, 3, "Постановление № 1", SearchScopeKind.CurrentCase, [3], "sha", 300, null, 5, 0.5,
            "yunet-1", "sface-1", 2, 1, null, DateTime.UtcNow, 1, SessionOrigin.SystemSuggestion, Person);
        var forHead = VerificationQueueItem.From(
            Candidate(Person, Decisions(VerificationVerdict.Confirmed, VerificationVerdict.Rejected)), session, Head, VerificationStage.Supervisor);
        forHead.SuggestedPersonRef.ShouldBe(Person);
        forHead.OwnDecision.ShouldBeNull();
    }

    private async Task<ResponseDto<SupervisorReview>> ReviewAsync() =>
        await new GetSupervisorReviewQuery.Handler(_subjects, _policy, _accessProvider, _store)
            .Handle(new GetSupervisorReviewQuery(11), CancellationToken.None);

    private static VerificationDecision Decision(int subject, VerificationStage stage, VerificationVerdict verdict) =>
        new(subject, stage, verdict, "признаки", DateTime.UtcNow);

    private static VerificationDecision[] Decisions(VerificationVerdict expert, VerificationVerdict verifier) =>
    [
        Decision(Expert, VerificationStage.Expert, expert),
        Decision(Verifier, VerificationStage.Verifier, verifier),
    ];

    private static SearchCandidateRow Candidate(int? personRef, IReadOnlyList<VerificationDecision> decisions) => new(
        Id: 11, SessionId: 5, CaseId: 3, Rank: 1, FaceId: 100, AssetId: 50, FrameIndex: null, FrameTimestampMs: null,
        CosineDistance: 0.2, CropStoredFileName: "crop.jpg", ModelVersion: "sface-1", Classification: 2, DivisionId: 1,
        Status: TwoPersonRule.Resolve(decisions), PersonRef: personRef, Decisions: decisions.ToList());

    private void GivenCandidate(int? personRef, params VerificationDecision[] decisions) =>
        _store.GetCandidateAsync(11, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>()).Returns(Candidate(personRef, decisions));

    private async Task<ResponseDto<CandidateStatus>> HandleAsync(RecordVerificationCommand command) =>
        await new RecordVerificationCommand.Handler(_subjects, _policy, _accessProvider, _store, _caseScope, _audit)
            .Handle(command, CancellationToken.None);
}
