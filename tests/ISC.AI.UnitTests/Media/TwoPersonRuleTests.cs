using System;
using ISC.AI.Modules.Media.Domain.Model;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Media;

/// <summary>Правило двух лиц (ТБ-073, GATE-5): чистая функция статуса кандидата и допустимости решения.</summary>
public sealed class TwoPersonRuleTests
{
    private static VerificationDecision Decision(int subject, VerificationStage stage, VerificationVerdict verdict) =>
        new(subject, stage, verdict, "признаки", DateTime.UtcNow);

    [Fact(DisplayName = "Без решений — «кандидат»; только эксперт — «ждёт верификатора» при любом исходе")]
    public void No_decisions_is_candidate_and_expert_alone_is_pending()
    {
        TwoPersonRule.Resolve([]).ShouldBe(CandidateStatus.Candidate);
        TwoPersonRule.Resolve([Decision(1, VerificationStage.Expert, VerificationVerdict.Confirmed)]).ShouldBe(CandidateStatus.PendingVerifier);
        TwoPersonRule.Resolve([Decision(1, VerificationStage.Expert, VerificationVerdict.Rejected)]).ShouldBe(CandidateStatus.PendingVerifier);
    }

    [Fact(DisplayName = "Два «подтверждён» РАЗНЫХ субъектов — «следственная версия» (Confirmed)")]
    public void Two_confirmations_by_different_subjects_confirm()
    {
        TwoPersonRule.Resolve([
            Decision(1, VerificationStage.Expert, VerificationVerdict.Confirmed),
            Decision(2, VerificationStage.Verifier, VerificationVerdict.Confirmed),
        ]).ShouldBe(CandidateStatus.Confirmed);
        TwoPersonRule.ConfirmedMarker.ShouldContain("следственная версия");
    }

    [Fact(DisplayName = "Тот же субъект на обеих стадиях — «неопределённо», а CanDecide отказывает с причиной")]
    public void Same_subject_on_both_stages_is_undetermined_and_refused()
    {
        var expert = Decision(1, VerificationStage.Expert, VerificationVerdict.Confirmed);
        TwoPersonRule.Resolve([expert, Decision(1, VerificationStage.Verifier, VerificationVerdict.Confirmed)])
            .ShouldBe(CandidateStatus.Undetermined);

        TwoPersonRule.CanDecide([expert], VerificationStage.Verifier, 1, out var reason).ShouldBeFalse();
        reason.ShouldNotBeNullOrWhiteSpace();
        reason.ShouldContain("ТБ-073");
    }

    [Fact(DisplayName = "Отклонён+отклонён — Rejected; подтверждён+отклонён — Undetermined; любое «неопределённо» — Undetermined")]
    public void Disagreement_and_undetermined_are_conservative()
    {
        TwoPersonRule.Resolve([
            Decision(1, VerificationStage.Expert, VerificationVerdict.Rejected),
            Decision(2, VerificationStage.Verifier, VerificationVerdict.Rejected),
        ]).ShouldBe(CandidateStatus.Rejected);

        TwoPersonRule.Resolve([
            Decision(1, VerificationStage.Expert, VerificationVerdict.Confirmed),
            Decision(2, VerificationStage.Verifier, VerificationVerdict.Rejected),
        ]).ShouldBe(CandidateStatus.Undetermined);

        TwoPersonRule.Resolve([
            Decision(1, VerificationStage.Expert, VerificationVerdict.Undetermined),
            Decision(2, VerificationStage.Verifier, VerificationVerdict.Confirmed),
        ]).ShouldBe(CandidateStatus.Undetermined);

        TwoPersonRule.Resolve([
            Decision(1, VerificationStage.Expert, VerificationVerdict.Confirmed),
            Decision(2, VerificationStage.Verifier, VerificationVerdict.Undetermined),
        ]).ShouldBe(CandidateStatus.Undetermined);
    }

    [Fact(DisplayName = "CanDecide: верификатор до эксперта — нет; повтор стадии — нет; штатный порядок — да")]
    public void Can_decide_enforces_order_and_single_decision_per_stage()
    {
        TwoPersonRule.CanDecide([], VerificationStage.Verifier, 2, out var beforeExpert).ShouldBeFalse();
        beforeExpert.ShouldNotBeNull();

        var expert = Decision(1, VerificationStage.Expert, VerificationVerdict.Confirmed);
        TwoPersonRule.CanDecide([expert], VerificationStage.Expert, 3, out var repeatExpert).ShouldBeFalse();
        repeatExpert.ShouldNotBeNull();

        var verifier = Decision(2, VerificationStage.Verifier, VerificationVerdict.Confirmed);
        TwoPersonRule.CanDecide([expert, verifier], VerificationStage.Verifier, 3, out var repeatVerifier).ShouldBeFalse();
        repeatVerifier.ShouldNotBeNull();

        TwoPersonRule.CanDecide([], VerificationStage.Expert, 1, out _).ShouldBeTrue();
        TwoPersonRule.CanDecide([expert], VerificationStage.Verifier, 2, out _).ShouldBeTrue();
    }
}
