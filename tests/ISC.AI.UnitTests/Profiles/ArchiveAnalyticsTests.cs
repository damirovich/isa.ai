using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Enums;
using ISC.AI.Abstractions.Grounding;
using ISC.AI.Abstractions.Rag;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Inspector.Application;
using ISC.AI.Profile.Inspector.Application.Features.Archive;
using ISC.AI.Profile.Inspector.Application.Features.Generation;
using ISC.AI.Profile.Inspector.Domain.Enums;
using ISC.AI.Profile.Inspector.Domain.Risk;
using ISC.AI.Profile.Inspector.Domain.Services;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Profiles;

/// <summary>
/// ИИ-аналитика архива (ТФ-АРХ-03): тренды (два смежных периода), повторяемость и разрез
/// «территориальные/линейные» считает КОД, ИИ вдумчивой ролью интерпретирует. Проверяется:
/// факт-блок несёт обе точки тренда со знаком, повторы и сравнение групп; при непомеченных
/// линейных сравнение честно объявляется невозможным; настоящий шаблон встроен; handler шлёт
/// роль Analysis и спрашивает ДВА периода; HITL всегда.
/// </summary>
public sealed class ArchiveAnalyticsTests
{
    private static readonly DashboardSummary Current = new(
        TotalViolations: 7, CriticalViolations: 2, NotRemediated: 4, Remediated: 3, [], []);

    private static readonly DashboardSummary Previous = new(
        TotalViolations: 4, CriticalViolations: 3, NotRemediated: 1, Remediated: 3, [], []);

    private static readonly DivisionRiskDetail NarynRisk = new(
        1, "Нарынская инспекция", ViolationCount: 5, OpenCount: 4, RepeatCount: 2, OverdueCount: 1,
        TrendDelta: 3, TopSpheres: ["Документооборот — 3"], LastRecommendation: null,
        Assessment: new RiskAssessment(19, RiskLevel.Medium, RiskColor.Yellow));

    private static readonly DivisionRiskDetail LineRisk = new(
        2, "Линейная инспекция связи", ViolationCount: 2, OpenCount: 1, RepeatCount: 0, OverdueCount: 0,
        TrendDelta: -1, TopSpheres: [], LastRecommendation: null,
        Assessment: new RiskAssessment(5, RiskLevel.Low, RiskColor.Green));

    private static readonly DivisionRemediationRow NarynRemediation = new(
        1, "Нарынская инспекция", Total: 5, Resolved: 1, Partial: 1, UnderControl: 2, Overdue: 1);

    private static readonly DivisionRemediationRow LineRemediation = new(
        2, "Линейная инспекция связи", Total: 2, Resolved: 1, Partial: 0, UnderControl: 1, Overdue: 0);

    private static readonly IReadOnlyDictionary<int, DivisionKind> Kinds = new Dictionary<int, DivisionKind>
    {
        [1] = DivisionKind.Territorial,
        [2] = DivisionKind.Linear,
    };

    [Fact(DisplayName = "Факт-блок: обе точки тренда со знаком, повторяемость и сравнение терр/лин")]
    public void Facts_carry_trend_repeats_and_kind_comparison()
    {
        var facts = ArchiveAnalyticsFacts.Build(
            Current, Previous, [NarynRisk, LineRisk], [NarynRemediation, LineRemediation], Kinds, 90);

        facts.ShouldContain("Текущий период: всего нарушений 7 (критических 2)");
        facts.ShouldContain("Предыдущий период: всего нарушений 4 (критических 3)");
        facts.ShouldContain("нарушений +3, критических -1");
        facts.ShouldContain("Нарынская инспекция: повторных 2 из 5");
        facts.ShouldContain("Документооборот — 3");
        facts.ShouldContain("Территориальное: подразделений с нарушениями 1; нарушений 5");
        facts.ShouldContain("Линейное: подразделений с нарушениями 1; нарушений 2");
        facts.ShouldContain("устранено 1 из 2");
        facts.ShouldContain("(линейное): нарушений 2");
    }

    [Fact(DisplayName = "Линейные не помечены — сравнение групп честно объявляется невозможным")]
    public void Missing_linear_kinds_are_stated()
    {
        var onlyTerritorial = new Dictionary<int, DivisionKind> { [1] = DivisionKind.Territorial };

        var facts = ArchiveAnalyticsFacts.Build(
            Current, Previous, [NarynRisk], [NarynRemediation], onlyTerritorial, 90);

        facts.ShouldContain("линейные подразделения в справочнике не помечены");
        facts.ShouldNotContain("Линейное: подразделений");
    }

    [Fact(DisplayName = "Аналитика: роль Analysis, два смежных периода, факт-блок в задачном промпте")]
    public async Task Handler_uses_analysis_role_and_two_periods()
    {
        var access = new AccessContext("u1", MaxClassification: 0, AllowedDivisions: [1]);
        var accessProvider = Substitute.For<IAccessContextProvider>();
        accessProvider.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(access);

        var dashboardCalls = new List<(DateOnly From, DateOnly To)>();
        var riskDataSource = Substitute.For<IRiskDataSource>();
        riskDataSource.GetDashboardAsync(
                Arg.Do<DateOnly>(f => dashboardCalls.Add((f, default))),
                Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(Current, Previous);
        riskDataSource.GetDivisionRisksAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<DivisionRiskDetail>)[NarynRisk, LineRisk]);
        riskDataSource.GetRemediationAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new RemediationSummary([], [NarynRemediation, LineRemediation]));

        var divisionStore = Substitute.For<IDivisionAdminStore>();
        divisionStore.ListAsync(Arg.Any<CancellationToken>()).Returns(
            (IReadOnlyList<DivisionNode>)
            [
                new DivisionNode(1, "Нарынская инспекция", null, null),
                new DivisionNode(2, "Линейная инспекция связи", null, null, Kind: DivisionKind.Linear),
            ]);

        GroundedRequest? captured = null;
        var generator = Substitute.For<IGroundedGenerator>();
        generator.GenerateAsync(Arg.Do<GroundedRequest>(r => captured = r), access, Arg.Any<CancellationToken>())
            .Returns(new GroundedResponse(
                "текст записки", new GroundingResult([], AllConfirmed: true), [], ResultClassification: 0));

        // Настоящий провайдер шаблонов — ловит опечатку в имени ресурса «archive-analytics».
        var provider = new EmbeddedScribanPromptProvider(
            typeof(InspectorApplicationServiceCollectionExtensions).Assembly);

        var response = await new GenerateArchiveAnalyticsCommand.Handler(
                generator, riskDataSource, divisionStore, accessProvider, provider)
            .Handle(new GenerateArchiveAnalyticsCommand(90), CancellationToken.None);

        // Тренд без второй точки — не тренд: сводка спрошена за ДВА смежных периода.
        dashboardCalls.Count.ShouldBe(2);
        dashboardCalls[1].From.ShouldBe(dashboardCalls[0].From.AddDays(-90));

        captured.ShouldNotBeNull();
        captured!.Role.ShouldBe(ModelRole.Analysis);
        captured.TaskPrompt.ShouldNotBeNull();
        captured.TaskPrompt.ShouldContain("Текущий период: всего нарушений 7"); // числа — из факт-блока,
        captured.TaskPrompt.ShouldContain("не выдумывай");                      // другие запрещены.
        captured.TaskPrompt.ShouldNotContain("{{");                             // подстановки отработали.

        response.Status.ShouldBeTrue();
        response.Data!.RequiresHumanReview.ShouldBeTrue();
    }
}
