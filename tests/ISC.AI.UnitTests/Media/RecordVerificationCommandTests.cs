using System;
using System.Collections.Generic;
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

/// <summary>Сценарий записи решения верификации (ТБ-073, GATE-5): отказы аудируются, подтверждение несёт обоих субъектов.</summary>
public sealed class RecordVerificationCommandTests
{
    private const int CurrentUser = 7;
    private const int OtherUser = 3;

    private readonly ISubjectProvider _subjects = Substitute.For<ISubjectProvider>();
    private readonly IVerificationPolicy _policy = Substitute.For<IVerificationPolicy>();
    private readonly IAccessContextProvider _accessProvider = Substitute.For<IAccessContextProvider>();
    private readonly ISearchSessionStore _store = Substitute.For<ISearchSessionStore>();
    private readonly ICaseScope _caseScope = Substitute.For<ICaseScope>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();

    public RecordVerificationCommandTests()
    {
        _subjects.GetCurrentUserIdAsync(Arg.Any<CancellationToken>()).Returns(CurrentUser);
        _policy.CanActAsync(Arg.Any<VerificationStage>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);
        _accessProvider.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(new AccessContext("7", 2, [1]));
    }

    [Fact(DisplayName = "Самоподтверждение: верификатор = эксперт → отказ, аудит decision-denied, решение не записано")]
    public async Task Self_confirmation_is_rejected_and_audited()
    {
        GivenCandidate(Decision(CurrentUser, VerificationStage.Expert, VerificationVerdict.Confirmed));

        var response = await HandleAsync(new RecordVerificationCommand(11, VerificationStage.Verifier, VerificationVerdict.Confirmed, "признаки"));

        response.Status.ShouldBeFalse();
        response.StatusCode.ShouldBe(ResponseStatusCode.BadRequest);
        response.StatusMessage.ShouldContain("ТБ-073");
        await _audit.Received(1).WriteAsync(
            Arg.Is<AuditEntry>(e => e.ObjectRef == "media:candidate:11:decision-denied" && e.SubjectId == CurrentUser && e.Action == AuditAction.Modify),
            Arg.Any<CancellationToken>());
        await AssertNoDecisionRecordedAsync();
    }

    [Fact(DisplayName = "Роль не даёт права стадии → отказ и аудит попытки")]
    public async Task Forbidden_role_is_rejected_and_audited()
    {
        _policy.CanActAsync(VerificationStage.Expert, CurrentUser, Arg.Any<CancellationToken>()).Returns(false);
        GivenCandidate();

        var response = await HandleAsync(new RecordVerificationCommand(11, VerificationStage.Expert, VerificationVerdict.Confirmed, "признаки"));

        response.StatusCode.ShouldBe(ResponseStatusCode.BadRequest);
        await _audit.Received(1).WriteAsync(
            Arg.Is<AuditEntry>(e => e.ObjectRef == "media:candidate:11:decision-denied"), Arg.Any<CancellationToken>());
        await AssertNoDecisionRecordedAsync();
    }

    [Fact(DisplayName = "Второе «подтверждён» другого субъекта → Confirmed, появление с обоими субъектами, маркер «следственная версия»")]
    public async Task Confirmation_records_appearance_with_both_subjects()
    {
        GivenCandidate(Decision(OtherUser, VerificationStage.Expert, VerificationVerdict.Confirmed));

        var response = await HandleAsync(new RecordVerificationCommand(11, VerificationStage.Verifier, VerificationVerdict.Confirmed, "признаки"));

        response.Status.ShouldBeTrue();
        response.Data.ShouldBe(CandidateStatus.Confirmed);
        await _store.Received(1).RecordDecisionAsync(
            11, Arg.Is<VerificationDecision>(d => d.SubjectId == CurrentUser && d.Stage == VerificationStage.Verifier),
            CandidateStatus.Confirmed, null, Arg.Any<CancellationToken>());
        await _caseScope.Received(1).RecordAppearanceAsync(
            Arg.Is<ConfirmedAppearance>(a => a.ExpertUserId == OtherUser && a.VerifierUserId == CurrentUser && a.PersonRef == 77 && a.CandidateId == 11),
            Arg.Any<CancellationToken>());
        await _audit.Received(1).WriteAsync(
            Arg.Is<AuditEntry>(e => e.ObjectRef == "media:candidate:11:decision:Verifier"
                && e.PayloadSensitive!.Contains("эксперт=3") && e.PayloadSensitive.Contains("верификатор=7")
                && e.PayloadSensitive.Contains(TwoPersonRule.ConfirmedMarker)),
            Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Верификатор не может передать фигуранта (слепота ТФ-ВЕР-02): отказ, аудит, без записи; валидатор тоже отклоняет")]
    public async Task Verifier_cannot_pass_person_ref()
    {
        GivenCandidate(Decision(OtherUser, VerificationStage.Expert, VerificationVerdict.Confirmed));

        var response = await HandleAsync(new RecordVerificationCommand(11, VerificationStage.Verifier, VerificationVerdict.Confirmed, "признаки", PersonRef: 5));

        response.StatusCode.ShouldBe(ResponseStatusCode.BadRequest);
        response.StatusMessage.ShouldContain("ТФ-ВЕР-02");
        await _audit.Received(1).WriteAsync(
            Arg.Is<AuditEntry>(e => e.ObjectRef == "media:candidate:11:decision-denied"), Arg.Any<CancellationToken>());
        await AssertNoDecisionRecordedAsync();

        var validation = new RecordVerificationValidator().Validate(
            new RecordVerificationCommand(11, VerificationStage.Verifier, VerificationVerdict.Confirmed, "признаки", PersonRef: 5));
        validation.IsValid.ShouldBeFalse();
    }

    [Fact(DisplayName = "Решение эксперта → PendingVerifier с фигурантом, появления нет; расхождение → Undetermined без появления")]
    public async Task Expert_decision_pending_and_disagreement_undetermined()
    {
        GivenCandidate();
        var first = await HandleAsync(new RecordVerificationCommand(11, VerificationStage.Expert, VerificationVerdict.Confirmed, "признаки", PersonRef: 77));
        first.Data.ShouldBe(CandidateStatus.PendingVerifier);
        await _store.Received(1).RecordDecisionAsync(11, Arg.Any<VerificationDecision>(), CandidateStatus.PendingVerifier, 77, Arg.Any<CancellationToken>());
        await _caseScope.DidNotReceive().RecordAppearanceAsync(Arg.Any<ConfirmedAppearance>(), Arg.Any<CancellationToken>());

        GivenCandidate(Decision(OtherUser, VerificationStage.Expert, VerificationVerdict.Confirmed));
        var second = await HandleAsync(new RecordVerificationCommand(11, VerificationStage.Verifier, VerificationVerdict.Rejected, "признаки не совпадают"));
        second.Data.ShouldBe(CandidateStatus.Undetermined);
        await _caseScope.DidNotReceive().RecordAppearanceAsync(Arg.Any<ConfirmedAppearance>(), Arg.Any<CancellationToken>());
    }

    private static VerificationDecision Decision(int subject, VerificationStage stage, VerificationVerdict verdict) =>
        new(subject, stage, verdict, "признаки", DateTime.UtcNow);

    private void GivenCandidate(params VerificationDecision[] decisions)
    {
        var row = new SearchCandidateRow(
            Id: 11, SessionId: 5, CaseId: 3, Rank: 1, FaceId: 100, AssetId: 50, FrameIndex: null, FrameTimestampMs: null,
            CosineDistance: 0.2, CropStoredFileName: "crop.jpg", ModelVersion: "sface-1", Classification: 2, DivisionId: 1,
            Status: TwoPersonRule.Resolve(decisions), PersonRef: 77, Decisions: new List<VerificationDecision>(decisions));
        _store.GetCandidateAsync(11, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>()).Returns(row);
    }

    private async Task AssertNoDecisionRecordedAsync() =>
        await _store.DidNotReceive().RecordDecisionAsync(
            Arg.Any<int>(), Arg.Any<VerificationDecision>(), Arg.Any<CandidateStatus>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());

    private async Task<ResponseDto<CandidateStatus>> HandleAsync(RecordVerificationCommand command)
    {
        var handler = new RecordVerificationCommand.Handler(_subjects, _policy, _accessProvider, _store, _caseScope, _audit);
        return await handler.Handle(command, CancellationToken.None);
    }
}
