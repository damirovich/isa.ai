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

/// <summary>
/// Запись решения в режиме одного оператора (ТФ-ВЕР-07, ТБ-072/073, GATE-5, ADR-0054): решение оператора окончательное
/// и создаёт появление с одной подписью; режим разбора берётся из настройки только у первого решения; смена режима не
/// пересчитывает начатые вдвоём разборы; обзор руководителя — после решения оператора.
/// </summary>
public sealed class SingleOperatorVerificationTests
{
    private const int CurrentUser = 7;
    private const int OtherUser = 3;

    private readonly ISubjectProvider _subjects = Substitute.For<ISubjectProvider>();
    private readonly IVerificationPolicy _policy = Substitute.For<IVerificationPolicy>();
    private readonly IAccessContextProvider _accessProvider = Substitute.For<IAccessContextProvider>();
    private readonly ISearchSessionStore _store = Substitute.For<ISearchSessionStore>();
    private readonly ICaseScope _caseScope = Substitute.For<ICaseScope>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private IMediaSettingsStore _settings = MediaTestSettings.Store(ConfirmationMode.SingleOperator);

    public SingleOperatorVerificationTests()
    {
        _subjects.GetCurrentUserIdAsync(Arg.Any<CancellationToken>()).Returns(CurrentUser);
        _policy.CanActAsync(Arg.Any<VerificationStage>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(true);
        _accessProvider.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(new AccessContext("7", 2, [1]));
        _caseScope.ListPersonsAsync(3, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>())
            .Returns([new CasePersonItem(77, "Фигурант 1")]);
    }

    [Fact(DisplayName = "Один оператор: «подтверждён» → статус сразу, появление с одной подписью, в журнале режим и подтвердивший")]
    public async Task Operator_confirmation_is_final_with_single_signature()
    {
        GivenCandidate();

        var response = await HandleAsync(new RecordVerificationCommand(11, VerificationStage.Expert, VerificationVerdict.Confirmed, "признаки", PersonRef: 77));

        response.Data.ShouldBe(CandidateStatus.Confirmed);
        await _store.Received(1).RecordDecisionAsync(
            11, Arg.Is<VerificationDecision>(d => d.SubjectId == CurrentUser && d.Mode == ConfirmationMode.SingleOperator),
            CandidateStatus.Confirmed, 77, Arg.Any<CancellationToken>());
        await _caseScope.Received(1).RecordAppearanceAsync(
            Arg.Is<ConfirmedAppearance>(a => a.ExpertUserId == CurrentUser && a.VerifierUserId == null && a.PersonRef == 77),
            Arg.Any<CancellationToken>());
        await _audit.Received(1).WriteAsync(
            Arg.Is<AuditEntry>(e => e.ObjectRef == "media:candidate:11:decision:Expert"
                && e.PayloadSensitive!.Contains("режим=один оператор") && e.PayloadSensitive.Contains("подтвердил=7")
                && e.PayloadSensitive.Contains(TwoPersonRule.ConfirmedMarker)),
            Arg.Any<CancellationToken>());
    }

    [Theory(DisplayName = "Один оператор: «отклонён» и «неопределённо» — итог сразу, без появления")]
    [InlineData(VerificationVerdict.Rejected, CandidateStatus.Rejected)]
    [InlineData(VerificationVerdict.Undetermined, CandidateStatus.Undetermined)]
    public async Task Operator_rejection_or_undetermined_is_final(VerificationVerdict verdict, CandidateStatus expected)
    {
        GivenCandidate();

        var response = await HandleAsync(new RecordVerificationCommand(11, VerificationStage.Expert, verdict, "признаки"));

        response.Data.ShouldBe(expected);
        await _caseScope.DidNotReceive().RecordAppearanceAsync(Arg.Any<ConfirmedAppearance>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Один оператор: вторая подпись по такому кандидату отклоняется и аудируется, решение не записывается")]
    public async Task Verifier_on_single_operator_candidate_is_refused()
    {
        GivenCandidate(Decision(OtherUser, VerificationStage.Expert, VerificationVerdict.Confirmed, ConfirmationMode.SingleOperator));

        var response = await HandleAsync(new RecordVerificationCommand(11, VerificationStage.Verifier, VerificationVerdict.Rejected, "признаки"));

        response.StatusCode.ShouldBe(ResponseStatusCode.BadRequest);
        response.StatusMessage.ShouldContain("ТФ-ВЕР-07");
        await _audit.Received(1).WriteAsync(
            Arg.Is<AuditEntry>(e => e.ObjectRef == "media:candidate:11:decision-denied" && e.PayloadSensitive!.Contains("одного оператора")),
            Arg.Any<CancellationToken>());
        await _store.DidNotReceive().RecordDecisionAsync(
            Arg.Any<int>(), Arg.Any<VerificationDecision>(), Arg.Any<CandidateStatus>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Смена режима: начатый вдвоём разбор доводится вдвоём — решение верификатора с режимом эксперта, две подписи")]
    public async Task Started_two_person_review_finishes_two_person()
    {
        // Эксперт решал при режиме двух лиц; Администратор с тех пор включил одного оператора.
        GivenCandidate(Decision(OtherUser, VerificationStage.Expert, VerificationVerdict.Confirmed, ConfirmationMode.TwoPerson));

        var response = await HandleAsync(new RecordVerificationCommand(11, VerificationStage.Verifier, VerificationVerdict.Confirmed, "признаки"));

        response.Data.ShouldBe(CandidateStatus.Confirmed);
        await _store.Received(1).RecordDecisionAsync(
            11, Arg.Is<VerificationDecision>(d => d.Mode == ConfirmationMode.TwoPerson), CandidateStatus.Confirmed, null, Arg.Any<CancellationToken>());
        await _caseScope.Received(1).RecordAppearanceAsync(
            Arg.Is<ConfirmedAppearance>(a => a.ExpertUserId == OtherUser && a.VerifierUserId == CurrentUser), Arg.Any<CancellationToken>());
        await _settings.DidNotReceive().GetAsync(Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Смена режима: кандидат без решений разбирается по действующему режиму — включены два лица, «подтверждён» ждёт верификатора")]
    public async Task New_review_uses_current_mode()
    {
        _settings = MediaTestSettings.Store(ConfirmationMode.TwoPerson);
        GivenCandidate();

        var response = await HandleAsync(new RecordVerificationCommand(11, VerificationStage.Expert, VerificationVerdict.Confirmed, "признаки", PersonRef: 77));

        response.Data.ShouldBe(CandidateStatus.PendingVerifier);
        await _store.Received(1).RecordDecisionAsync(
            11, Arg.Is<VerificationDecision>(d => d.Mode == ConfirmationMode.TwoPerson), CandidateStatus.PendingVerifier, 77, Arg.Any<CancellationToken>());
        await _caseScope.DidNotReceive().RecordAppearanceAsync(Arg.Any<ConfirmedAppearance>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Один оператор, «неопределённо»: руководитель-третье лицо подтверждает — появление с его единственной подписью")]
    public async Task Supervisor_confirms_single_operator_undetermined()
    {
        GivenCandidate(Decision(OtherUser, VerificationStage.Expert, VerificationVerdict.Undetermined, ConfirmationMode.SingleOperator));

        var response = await HandleAsync(new RecordVerificationCommand(11, VerificationStage.Supervisor, VerificationVerdict.Confirmed, "признаки", PersonRef: 77));

        response.Data.ShouldBe(CandidateStatus.Confirmed);
        await _caseScope.Received(1).RecordAppearanceAsync(
            Arg.Is<ConfirmedAppearance>(a => a.ExpertUserId == CurrentUser && a.VerifierUserId == null), Arg.Any<CancellationToken>());
        await _audit.Received(1).WriteAsync(
            Arg.Is<AuditEntry>(e => e.ObjectRef == "media:candidate:11:decision:Supervisor"
                && e.PayloadSensitive!.Contains("режим=один оператор") && !e.PayloadSensitive.Contains("верификатор=")),
            Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Один оператор: обзор руководителя доступен после решения оператора — второй подписи не ждёт")]
    public async Task Supervisor_review_after_operator_decision()
    {
        GivenCandidate(Decision(OtherUser, VerificationStage.Expert, VerificationVerdict.Undetermined, ConfirmationMode.SingleOperator));
        var handler = new GetSupervisorReviewQuery.Handler(_subjects, _policy, _accessProvider, _store);

        var review = await handler.Handle(new GetSupervisorReviewQuery(11), CancellationToken.None);

        review.Status.ShouldBeTrue();
        review.Data!.Decisions.ShouldHaveSingleItem().Mode.ShouldBe(ConfirmationMode.SingleOperator);

        // Два лица: пока нет верификатора — обзора нет (слепота второй проверки).
        GivenCandidate(Decision(OtherUser, VerificationStage.Expert, VerificationVerdict.Undetermined, ConfirmationMode.TwoPerson));
        (await handler.Handle(new GetSupervisorReviewQuery(11), CancellationToken.None)).StatusCode.ShouldBe(ResponseStatusCode.NotFound);
    }

    private static VerificationDecision Decision(int subject, VerificationStage stage, VerificationVerdict verdict, ConfirmationMode mode) =>
        new(subject, stage, verdict, "признаки", DateTime.UtcNow, mode);

    private void GivenCandidate(params VerificationDecision[] decisions)
    {
        var row = new SearchCandidateRow(
            Id: 11, SessionId: 5, CaseId: 3, Rank: 1, FaceId: 100, AssetId: 50, FrameIndex: null, FrameTimestampMs: null,
            CosineDistance: 0.2, CropStoredFileName: "crop.jpg", ModelVersion: "sface-1", Classification: 2, DivisionId: 1,
            Status: TwoPersonRule.Resolve(decisions), PersonRef: 77, Decisions: new List<VerificationDecision>(decisions));
        _store.GetCandidateAsync(11, Arg.Any<AccessContext>(), Arg.Any<CancellationToken>()).Returns(row);
    }

    private async Task<ResponseDto<CandidateStatus>> HandleAsync(RecordVerificationCommand command) =>
        await new RecordVerificationCommand.Handler(_subjects, _policy, _accessProvider, _store, _caseScope, _audit, _settings)
            .Handle(command, CancellationToken.None);
}
