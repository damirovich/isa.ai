using System;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Enums;
using ISC.AI.Abstractions.Grounding;
using ISC.AI.Abstractions.Rag;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Inspector.Application;
using ISC.AI.Profile.Inspector.Application.Features.Collegium;
using ISC.AI.Profile.Inspector.Application.Features.Generation;
using ISC.AI.Profile.Inspector.Domain.Enums;
using ISC.AI.Profile.Inspector.Domain.Risk;
using ISC.AI.Profile.Inspector.Domain.Services;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Profiles;

/// <summary>
/// Материалы коллегии (ТФ-КОЛ-01/02, §5.2.8): числа считает код (факт-блок общий с отчётом
/// руководству), ИИ быстрой ролью пишет только текст под структуру вида. Проверяется: каждый вид
/// берёт СВОЙ встроенный шаблон (доклад/решение/материалы), факт-блок внутри задачного промпта,
/// роль Draft, HITL всегда; проект решения запрещает выдумывать сроки.
/// </summary>
public sealed class CollegiumMaterialTests
{
    private static readonly DashboardSummary Dashboard = new(
        TotalViolations: 7, CriticalViolations: 2, NotRemediated: 4, Remediated: 3, [], []);

    private static readonly DivisionRiskDetail NarynRisk = new(
        1, "Нарынская инспекция", ViolationCount: 5, OpenCount: 4, RepeatCount: 2, OverdueCount: 1,
        TrendDelta: 3, TopSpheres: ["Документооборот — 3"], LastRecommendation: null,
        Assessment: new RiskAssessment(19, RiskLevel.Medium, RiskColor.Yellow));

    private static readonly DivisionRemediationRow NarynRemediation = new(
        1, "Нарынская инспекция", Total: 5, Resolved: 1, Partial: 1, UnderControl: 2, Overdue: 1);

    [Theory(DisplayName = "Каждый вид материала берёт свой шаблон: заголовок вида — в задачном промпте")]
    [InlineData(CollegiumMaterialKind.Report, "ДОКЛАД на заседание коллегии")]
    [InlineData(CollegiumMaterialKind.DraftDecision, "ПРОЕКТ РЕШЕНИЯ КОЛЛЕГИИ")]
    [InlineData(CollegiumMaterialKind.Briefing, "МАТЕРИАЛЫ К СОВЕЩАНИЮ РУКОВОДСТВА")]
    public async Task Each_kind_uses_its_template(CollegiumMaterialKind kind, string heading)
    {
        var (captured, response) = await RunAsync(kind);

        captured.ShouldNotBeNull();
        captured!.Role.ShouldBe(ModelRole.Draft);
        captured.TaskPrompt.ShouldNotBeNull();
        captured.TaskPrompt.ShouldContain(heading);                       // структура — своего вида,
        captured.TaskPrompt.ShouldContain("Всего нарушений: 7");          // числа — из факт-блока,
        captured.TaskPrompt.ShouldContain("не выдумывай");                // другие запрещены.
        captured.TaskPrompt.ShouldNotContain("{{");                       // подстановки отработали.

        response.Status.ShouldBeTrue();
        response.Data!.RequiresHumanReview.ShouldBeTrue();
    }

    [Fact(DisplayName = "Проект решения: календарные сроки ИИ не назначает — место для человека")]
    public async Task Draft_decision_forbids_invented_deadlines()
    {
        var (captured, _) = await RunAsync(CollegiumMaterialKind.DraftDecision);

        captured!.TaskPrompt!.ShouldContain("в срок до ______");
        captured.TaskPrompt.ShouldContain("НЕ придумывай");
    }

    private static async Task<(GroundedRequest? Captured,
        ISC.AI.Abstractions.Application.ResponseDto<GenerateReferenceResult> Response)> RunAsync(
        CollegiumMaterialKind kind)
    {
        var access = new AccessContext("u1", MaxClassification: 0, AllowedDivisions: [1]);
        var accessProvider = Substitute.For<IAccessContextProvider>();
        accessProvider.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(access);

        var riskDataSource = Substitute.For<IRiskDataSource>();
        riskDataSource.GetDashboardAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<DashboardFilter?>(), Arg.Any<CancellationToken>())
            .Returns(Dashboard);
        riskDataSource.GetDivisionRisksAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<DivisionRiskDetail>)[NarynRisk]);
        riskDataSource.GetRemediationAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new RemediationSummary([], [NarynRemediation]));

        GroundedRequest? captured = null;
        var generator = Substitute.For<IGroundedGenerator>();
        generator.GenerateAsync(Arg.Do<GroundedRequest>(r => captured = r), access, Arg.Any<CancellationToken>())
            .Returns(new GroundedResponse(
                "текст материала", new GroundingResult([], AllConfirmed: true), [], ResultClassification: 0));

        // Настоящий провайдер шаблонов — ловит опечатку в имени ресурса «collegium-*».
        var provider = new EmbeddedScribanPromptProvider(
            typeof(InspectorApplicationServiceCollectionExtensions).Assembly);

        var response = await new GenerateCollegiumMaterialCommand.Handler(
                generator, riskDataSource, accessProvider, provider)
            .Handle(new GenerateCollegiumMaterialCommand(kind, 90), CancellationToken.None);

        return (captured, response);
    }
}
