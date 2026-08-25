using System;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Enums;
using ISC.AI.Abstractions.Grounding;
using ISC.AI.Abstractions.Rag;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Inspector.Application;
using ISC.AI.Profile.Inspector.Application.Features.Generation;
using ISC.AI.Profile.Inspector.Application.Features.Risks;
using ISC.AI.Profile.Inspector.Domain.Enums;
using ISC.AI.Profile.Inspector.Domain.Risk;
using ISC.AI.Profile.Inspector.Domain.Services;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Profiles;

/// <summary>
/// Правила и критерии оценки (ТФ-РСК-02): факт-блок перечисляет ТОЛЬКО показатели, которые система
/// реально считает, и текущую картину применения; ИИ быстрой ролью оформляет их в положение,
/// НЕ назначая порогов. Проверяется: факт-блок несёт показатели и уровни словами; настоящий шаблон
/// «control-rules» встроен; роль Draft; запрет порогов в промпте; HITL всегда.
/// </summary>
public sealed class ControlRulesTests
{
    private static readonly DivisionRiskDetail NarynRisk = new(
        1, "Нарынская инспекция", ViolationCount: 5, OpenCount: 4, RepeatCount: 2, OverdueCount: 1,
        TrendDelta: 3, TopSpheres: ["Документооборот — 3"], LastRecommendation: null,
        Assessment: new RiskAssessment(19, RiskLevel.Medium, RiskColor.Yellow));

    [Fact(DisplayName = "Факт-блок: показатели системы, процедуры и картина применения с уровнем словами")]
    public void Facts_carry_indicators_and_current_picture()
    {
        var facts = ControlRulesFacts.Build([NarynRisk], 90);

        facts.ShouldContain("повторные нарушения");
        facts.ShouldContain("контрольный срок устранения истёк");
        facts.ShouldContain("итоговый балл риска по детерминированной формуле");
        facts.ShouldContain("автоматическая пометка просрочки");
        facts.ShouldContain("Нарынская инспекция: уровень Средний (балл 19)");
    }

    [Fact(DisplayName = "Пустой период — картина применения честно названа пустой")]
    public void Empty_period_is_stated()
    {
        var facts = ControlRulesFacts.Build([], 30);
        facts.ShouldContain("светофор пуст");
    }

    [Fact(DisplayName = "Правила: роль Draft, факт-блок в промпте, пороги — место для человека")]
    public async Task Handler_uses_fast_role_and_blank_thresholds()
    {
        var access = new AccessContext("u1", MaxClassification: 0, AllowedDivisions: [1]);
        var accessProvider = Substitute.For<IAccessContextProvider>();
        accessProvider.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(access);

        var riskDataSource = Substitute.For<IRiskDataSource>();
        riskDataSource.GetDivisionRisksAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<DivisionRiskDetail>)[NarynRisk]);

        GroundedRequest? captured = null;
        var generator = Substitute.For<IGroundedGenerator>();
        generator.GenerateAsync(Arg.Do<GroundedRequest>(r => captured = r), access, Arg.Any<CancellationToken>())
            .Returns(new GroundedResponse(
                "текст правил", new GroundingResult([], AllConfirmed: true), [], ResultClassification: 0));

        // Настоящий провайдер шаблонов — ловит опечатку в имени ресурса «control-rules».
        var provider = new EmbeddedScribanPromptProvider(
            typeof(InspectorApplicationServiceCollectionExtensions).Assembly);

        var response = await new GenerateControlRulesCommand.Handler(
                generator, riskDataSource, accessProvider, provider)
            .Handle(new GenerateControlRulesCommand(90), CancellationToken.None);

        captured.ShouldNotBeNull();
        captured!.Role.ShouldBe(ModelRole.Draft);
        captured.TaskPrompt.ShouldNotBeNull();
        captured.TaskPrompt.ShouldContain("ПРОЕКТ ПРАВИЛ ВНУТРЕННЕГО КОНТРОЛЯ");  // структура положения,
        captured.TaskPrompt.ShouldContain("повторные нарушения");                  // показатели — из факт-блока,
        captured.TaskPrompt.ShouldContain("порог: ______");                        // пороги утверждает человек.
        captured.TaskPrompt.ShouldNotContain("{{");                                // подстановки отработали.

        response.Status.ShouldBeTrue();
        response.Data!.DraftText.ShouldBe("текст правил");
        response.Data.RequiresHumanReview.ShouldBeTrue();
    }
}
