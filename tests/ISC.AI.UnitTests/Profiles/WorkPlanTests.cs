using System;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Enums;
using ISC.AI.Abstractions.Grounding;
using ISC.AI.Abstractions.Rag;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Inspector.Application;
using ISC.AI.Profile.Inspector.Application.Features.Generation;
using ISC.AI.Profile.Inspector.Application.Features.Methods;
using ISC.AI.Profile.Inspector.Domain.Enums;
using ISC.AI.Profile.Inspector.Domain.Risk;
using ISC.AI.Profile.Inspector.Domain.Services;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Profiles;

/// <summary>
/// План работы инспекции (ТФ-МЕТ-02): основания плана — проблемные зоны живого учёта (факт-блок
/// общий с отчётом руководству), ИИ быстрой ролью раскладывает их в план, НЕ назначая сроков.
/// Проверяется: настоящий шаблон «work-plan» встроен; роль Draft; факт-блок и запрет выдуманных
/// дат в промпте; HITL всегда.
/// </summary>
public sealed class WorkPlanTests
{
    [Fact(DisplayName = "План: роль Draft, факт-блок в промпте, сроки — место для человека")]
    public async Task Handler_uses_fast_role_and_blank_deadlines()
    {
        var access = new AccessContext("u1", MaxClassification: 0, AllowedDivisions: [1]);
        var accessProvider = Substitute.For<IAccessContextProvider>();
        accessProvider.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(access);

        var riskDataSource = Substitute.For<IRiskDataSource>();
        riskDataSource.GetDashboardAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<DashboardFilter?>(), Arg.Any<CancellationToken>())
            .Returns(new DashboardSummary(7, 2, 4, 3, [], []));
        riskDataSource.GetDivisionRisksAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<DivisionRiskDetail>)
            [
                new DivisionRiskDetail(1, "Нарынская инспекция", 5, 4, 2, 1, 3, ["Документооборот — 3"], null,
                    new RiskAssessment(19, RiskLevel.Medium, RiskColor.Yellow)),
            ]);
        riskDataSource.GetRemediationAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new RemediationSummary([], [new DivisionRemediationRow(1, "Нарынская инспекция", 5, 1, 1, 2, 1)]));

        GroundedRequest? captured = null;
        var generator = Substitute.For<IGroundedGenerator>();
        generator.GenerateAsync(Arg.Do<GroundedRequest>(r => captured = r), access, Arg.Any<CancellationToken>())
            .Returns(new GroundedResponse(
                "текст плана", new GroundingResult([], AllConfirmed: true), [], ResultClassification: 0));

        // Настоящий провайдер шаблонов — ловит опечатку в имени ресурса «work-plan».
        var provider = new EmbeddedScribanPromptProvider(
            typeof(InspectorApplicationServiceCollectionExtensions).Assembly);

        var response = await new GenerateWorkPlanCommand.Handler(
                generator, riskDataSource, accessProvider, provider)
            .Handle(new GenerateWorkPlanCommand(90), CancellationToken.None);

        captured.ShouldNotBeNull();
        captured!.Role.ShouldBe(ModelRole.Draft);
        captured.TaskPrompt.ShouldNotBeNull();
        captured.TaskPrompt.ShouldContain("ПРОЕКТ ПЛАНА РАБОТЫ ИНСПЕКЦИИ"); // структура плана,
        captured.TaskPrompt.ShouldContain("Всего нарушений: 7");            // числа — из факт-блока,
        captured.TaskPrompt.ShouldContain("в срок до ______");              // сроки вписывает человек,
        captured.TaskPrompt.ShouldContain("ответственный: ______");         // ответственных — тоже.
        captured.TaskPrompt.ShouldNotContain("{{");                         // подстановки отработали.

        response.Status.ShouldBeTrue();
        response.Data!.DraftText.ShouldBe("текст плана");
        response.Data.RequiresHumanReview.ShouldBeTrue();
    }
}
