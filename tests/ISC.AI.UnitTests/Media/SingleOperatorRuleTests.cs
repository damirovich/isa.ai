using System;
using ISC.AI.Modules.Media.Domain.Model;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Media;

/// <summary>
/// Правило подтверждения в режиме одного оператора (ТФ-ВЕР-07, ТБ-073, GATE-5, ADR-0054): решение оператора окончательное,
/// «неопределённо» — руководителю третьим лицом; режим разбора — режим решения эксперта, а не текущая настройка.
/// </summary>
public sealed class SingleOperatorRuleTests
{
    private const int Operator = 1;
    private const int Head = 3;

    private static VerificationDecision Single(int subject, VerificationStage stage, VerificationVerdict verdict) =>
        new(subject, stage, verdict, "признаки", DateTime.UtcNow, ConfirmationMode.SingleOperator);

    private static VerificationDecision Two(int subject, VerificationStage stage, VerificationVerdict verdict) =>
        new(subject, stage, verdict, "признаки", DateTime.UtcNow, ConfirmationMode.TwoPerson);

    [Theory(DisplayName = "Один оператор: исход оператора и есть статус — подтверждён, отклонён, неопределённо")]
    [InlineData(VerificationVerdict.Confirmed, CandidateStatus.Confirmed)]
    [InlineData(VerificationVerdict.Rejected, CandidateStatus.Rejected)]
    [InlineData(VerificationVerdict.Undetermined, CandidateStatus.Undetermined)]
    public void Operator_verdict_is_final(VerificationVerdict verdict, CandidateStatus expected)
    {
        TwoPersonRule.Resolve([Single(Operator, VerificationStage.Expert, verdict)]).ShouldBe(expected);
        TwoPersonRule.ModeOf([Single(Operator, VerificationStage.Expert, verdict)]).ShouldBe(ConfirmationMode.SingleOperator);
    }

    [Fact(DisplayName = "Без решений «подтверждён» недостижим ни в одном режиме; режима разбора ещё нет")]
    public void No_decision_no_status()
    {
        TwoPersonRule.Resolve([]).ShouldBe(CandidateStatus.Candidate);
        TwoPersonRule.ModeOf([]).ShouldBeNull();
    }

    [Fact(DisplayName = "Один оператор: вторая подпись не принимается — стадия верификатора отклоняется с причиной")]
    public void Verifier_stage_is_refused()
    {
        var decisions = new[] { Single(Operator, VerificationStage.Expert, VerificationVerdict.Confirmed) };

        TwoPersonRule.CanDecide(decisions, VerificationStage.Verifier, 2, out var reason).ShouldBeFalse();
        reason.ShouldNotBeNull().ShouldContain("одного оператора");

        // Даже если строка верификатора проскочила бы мимо проверки, статус — по решению оператора.
        TwoPersonRule.Resolve([.. decisions, Single(2, VerificationStage.Verifier, VerificationVerdict.Rejected)])
            .ShouldBe(CandidateStatus.Confirmed);
    }

    [Fact(DisplayName = "«Неопределённо» оператора: руководитель-третье лицо подтверждает или отклоняет, «подтверждён» ему доступен")]
    public void Supervisor_resolves_undetermined()
    {
        var undetermined = Single(Operator, VerificationStage.Expert, VerificationVerdict.Undetermined);

        TwoPersonRule.CanDecide([undetermined], VerificationStage.Supervisor, Head, out _).ShouldBeTrue();
        TwoPersonRule.CanSupervisorDecide([undetermined], VerificationVerdict.Confirmed, Head, out _).ShouldBeTrue();
        TwoPersonRule.CanSupervisorDecide([undetermined], VerificationVerdict.Rejected, Head, out _).ShouldBeTrue();
        TwoPersonRule.CanSupervisorDecide([undetermined], VerificationVerdict.Undetermined, Head, out _).ShouldBeFalse();

        TwoPersonRule.Resolve([undetermined, Single(Head, VerificationStage.Supervisor, VerificationVerdict.Confirmed)])
            .ShouldBe(CandidateStatus.Confirmed);
        TwoPersonRule.Resolve([undetermined, Single(Head, VerificationStage.Supervisor, VerificationVerdict.Rejected)])
            .ShouldBe(CandidateStatus.Rejected);
    }

    [Fact(DisplayName = "Оператор не снимает свою же неопределённость: как руководитель он отклоняется, его решение статус не меняет")]
    public void Operator_cannot_resolve_own_undetermined()
    {
        var undetermined = Single(Operator, VerificationStage.Expert, VerificationVerdict.Undetermined);

        TwoPersonRule.CanDecide([undetermined], VerificationStage.Supervisor, Operator, out var reason).ShouldBeFalse();
        reason.ShouldNotBeNullOrWhiteSpace();
        TwoPersonRule.Resolve([undetermined, Single(Operator, VerificationStage.Supervisor, VerificationVerdict.Confirmed)])
            .ShouldBe(CandidateStatus.Undetermined);
    }

    [Fact(DisplayName = "Решённого оператором руководитель не пересматривает: «подтверждён» и «отклонён» — не к руководителю")]
    public void Decided_candidate_is_not_for_supervisor()
    {
        TwoPersonRule.CanDecide([Single(Operator, VerificationStage.Expert, VerificationVerdict.Confirmed)],
            VerificationStage.Supervisor, Head, out _).ShouldBeFalse();
        TwoPersonRule.CanDecide([Single(Operator, VerificationStage.Expert, VerificationVerdict.Rejected)],
            VerificationStage.Supervisor, Head, out _).ShouldBeFalse();
    }

    [Fact(DisplayName = "Смена режима не пересчитывает: разбор, начатый вдвоём, — по правилам двух лиц, какой бы ни была настройка")]
    public void Two_person_review_stays_two_person()
    {
        var expert = Two(Operator, VerificationStage.Expert, VerificationVerdict.Confirmed);

        TwoPersonRule.Resolve([expert]).ShouldBe(CandidateStatus.PendingVerifier);
        TwoPersonRule.CanDecide([expert], VerificationStage.Verifier, 2, out _).ShouldBeTrue();
        TwoPersonRule.CanDecide([expert], VerificationStage.Verifier, Operator, out _).ShouldBeFalse();

        // Решение верификатора несёт режим эксперта; руководитель без второго «подтверждён» не подтверждает.
        var split = new[] { expert, Two(2, VerificationStage.Verifier, VerificationVerdict.Rejected) };
        TwoPersonRule.Resolve(split).ShouldBe(CandidateStatus.Undetermined);
        TwoPersonRule.CanSupervisorDecide([Two(Operator, VerificationStage.Expert, VerificationVerdict.Rejected),
            Two(2, VerificationStage.Verifier, VerificationVerdict.Undetermined)], VerificationVerdict.Confirmed, Head, out _).ShouldBeFalse();
    }

    [Fact(DisplayName = "Неизвестное значение режима читается как «два лица» — наиболее строгий вывод")]
    public void Unknown_mode_is_two_person()
    {
        var odd = new VerificationDecision(Operator, VerificationStage.Expert, VerificationVerdict.Confirmed, "признаки", DateTime.UtcNow,
            (ConfirmationMode)7);

        TwoPersonRule.Resolve([odd]).ShouldBe(CandidateStatus.PendingVerifier);
    }
}
