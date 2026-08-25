using System;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Enums;
using ISC.AI.Abstractions.Grounding;
using ISC.AI.Abstractions.Rag;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Inspector.Application;
using ISC.AI.Profile.Inspector.Application.Features.Generation;
using ISC.AI.Profile.Inspector.Application.Features.Monitoring;
using ISC.AI.Profile.Inspector.Domain.Enums;
using ISC.AI.Profile.Inspector.Domain.Risk;
using ISC.AI.Profile.Inspector.Domain.Services;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Profiles;

/// <summary>
/// Отчёт руководству (ТФ-МОН-02): ЧИСЛА отчёта считает код (факт-блок), ИИ быстрой ролью пишет
/// только текст. Проверяется: факт-блок несёт все цифры и уровни словами; настоящий шаблон встроен;
/// handler отдаёт модель Draft с факт-блоком в задачном промпте; результат всегда HITL.
/// </summary>
public sealed class ManagementReportTests
{
    private static readonly DashboardSummary Dashboard = new(
        TotalViolations: 7, CriticalViolations: 2, NotRemediated: 4, Remediated: 3, [], []);

    private static readonly DivisionRiskDetail NarynRisk = new(
        1, "Нарынская инспекция", ViolationCount: 5, OpenCount: 4, RepeatCount: 2, OverdueCount: 1,
        TrendDelta: 3, TopSpheres: ["Документооборот — 3"], LastRecommendation: null,
        Assessment: new RiskAssessment(19, RiskLevel.Medium, RiskColor.Yellow));

    private static readonly DivisionRemediationRow NarynRemediation = new(
        1, "Нарынская инспекция", Total: 5, Resolved: 1, Partial: 1, UnderControl: 2, Overdue: 1);

    [Fact(DisplayName = "Факт-блок несёт счётчики, уровень словами, сигналы и болевые сферы")]
    public void Facts_carry_all_numbers()
    {
        var facts = ManagementReportFacts.Build(Dashboard, [NarynRisk], [NarynRemediation], 90);

        facts.ShouldContain("Всего нарушений: 7");
        facts.ShouldContain("критических: 2");
        facts.ShouldContain("Нарынская инспекция: уровень Средний (балл 19)");
        facts.ShouldContain("повторных 2");
        facts.ShouldContain("динамика к прошлому периоду +3");
        facts.ShouldContain("Документооборот — 3");
        facts.ShouldContain("просрочено 1");
    }

    [Fact(DisplayName = "Без нарушений факт-блок честно говорит о пустом светофоре")]
    public void Empty_period_is_stated()
    {
        var facts = ManagementReportFacts.Build(
            new DashboardSummary(0, 0, 0, 0, [], []), [], [], 30);
        facts.ShouldContain("светофор пуст");
    }

    [Fact(DisplayName = "Отчёт: роль Draft, факт-блок внутри задачного промпта, результат — HITL")]
    public async Task Handler_uses_fast_role_and_embeds_facts()
    {
        var access = new AccessContext("u1", MaxClassification: 0, AllowedDivisions: [1]);
        var accessProvider = Substitute.For<IAccessContextProvider>();
        accessProvider.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(access);

        var riskDataSource = Substitute.For<IRiskDataSource>();
        riskDataSource.GetDashboardAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(Dashboard);
        riskDataSource.GetDivisionRisksAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<DivisionRiskDetail>)[NarynRisk]);
        riskDataSource.GetRemediationAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new RemediationSummary([], [NarynRemediation]));

        GroundedRequest? captured = null;
        var generator = Substitute.For<IGroundedGenerator>();
        generator.GenerateAsync(Arg.Do<GroundedRequest>(r => captured = r), access, Arg.Any<CancellationToken>())
            .Returns(new GroundedResponse(
                "текст отчёта", new GroundingResult([], AllConfirmed: true), [], ResultClassification: 0));

        // Настоящий провайдер шаблонов — ловит опечатку в имени ресурса «management-report».
        var provider = new EmbeddedScribanPromptProvider(
            typeof(InspectorApplicationServiceCollectionExtensions).Assembly);

        var response = await new GenerateManagementReportCommand.Handler(
                generator, riskDataSource, accessProvider, provider)
            .Handle(new GenerateManagementReportCommand(90), CancellationToken.None);

        captured.ShouldNotBeNull();
        captured!.Role.ShouldBe(ModelRole.Draft);
        captured.TaskPrompt.ShouldNotBeNull();
        captured.TaskPrompt.ShouldContain("Всего нарушений: 7");         // числа — из факт-блока,
        captured.TaskPrompt.ShouldContain("не выдумывай");               // и другие модели запрещены.
        captured.TaskPrompt.ShouldNotContain("{{");                      // подстановки отработали.

        response.Status.ShouldBeTrue();
        response.Data!.DraftText.ShouldBe("текст отчёта");
        response.Data.RequiresHumanReview.ShouldBeTrue();
    }
}
