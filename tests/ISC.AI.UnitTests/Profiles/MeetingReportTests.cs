using System;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Enums;
using ISC.AI.Abstractions.Grounding;
using ISC.AI.Abstractions.Rag;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Inspector.Application;
using ISC.AI.Profile.Inspector.Application.Features.Generation;
using ISC.AI.Profile.Inspector.Application.Features.Meetings;
using ISC.AI.Profile.Inspector.Domain.Services;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Profiles;

/// <summary>
/// Справка об исполнении поручений (ТФ-СОВ-02, §5.2.7): состояние пунктов протокола считает КОД
/// из документооборота, ИИ быстрой ролью пишет только текст. Проверяется: факт-блок несёт счётчики
/// и пометку эскалации; настоящий шаблон «meeting-report» встроен; handler отдаёт роль Draft
/// с факт-блоком в задачном промпте; гриф справки не ниже грифа протокола; недоступный протокол —
/// NotFound (неотличим от несуществующего).
/// </summary>
public sealed class MeetingReportTests
{
    private static readonly MeetingProtocol Protocol = new(
        DocumentId: 10, RegNumber: "ПС-7", RegDate: new DateOnly(2026, 8, 1),
        ShortContent: "Протокол совещания по итогам полугодия", Classification: 2,
        Items:
        [
            new MeetingItem(1, "Нарынская инспекция", new DateOnly(2026, 8, 15),
                "Исполнено", IsDone: true, IsOverdue: false),
            new MeetingItem(2, "Ошская инспекция", new DateOnly(2026, 8, 10),
                "Просрочено", IsDone: false, IsOverdue: true),
            new MeetingItem(3, "Таласская инспекция", null,
                "В работе", IsDone: false, IsOverdue: false),
        ]);

    [Fact(DisplayName = "Факт-блок несёт счётчики пунктов, сроки и пометку эскалации по просроченным")]
    public void Facts_carry_counters_and_escalation()
    {
        var facts = MeetingReportFacts.Build(Protocol);

        facts.ShouldContain("№ ПС-7");
        facts.ShouldContain("от 01.08.2026");
        facts.ShouldContain("Пунктов (поручений): 3; исполнено: 1; в работе: 1; ПРОСРОЧЕНО: 1.");
        facts.ShouldContain("Пункт 1: Нарынская инспекция; срок 15.08.2026; состояние: Исполнено.");
        facts.ShouldContain("Пункт 2: Ошская инспекция; срок 10.08.2026; состояние: Просрочено (ТРЕБУЕТ ЭСКАЛАЦИИ).");
        facts.ShouldContain("Пункт 3: Таласская инспекция; срок не установлен; состояние: В работе.");
    }

    [Fact(DisplayName = "Протокол без поручений — факт-блок честно говорит об этом")]
    public void Empty_protocol_is_stated()
    {
        var facts = MeetingReportFacts.Build(Protocol with { Items = [] });

        facts.ShouldContain("Поручений по протоколу не назначено.");
        facts.ShouldContain("Пунктов (поручений): 0");
    }

    [Fact(DisplayName = "Справка: роль Draft, факт-блок в задачном промпте, гриф не ниже грифа протокола")]
    public async Task Handler_uses_fast_role_and_inherits_protocol_classification()
    {
        var access = new AccessContext("u1", MaxClassification: 2, AllowedDivisions: [1]);
        var accessProvider = Substitute.For<IAccessContextProvider>();
        accessProvider.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(access);

        var reader = Substitute.For<IMeetingProtocolReader>();
        reader.ReadAsync(10, Arg.Any<CancellationToken>()).Returns(Protocol);

        GroundedRequest? captured = null;
        var generator = Substitute.For<IGroundedGenerator>();
        generator.GenerateAsync(Arg.Do<GroundedRequest>(r => captured = r), access, Arg.Any<CancellationToken>())
            .Returns(new GroundedResponse(
                "текст справки", new GroundingResult([], AllConfirmed: true), [], ResultClassification: 0));

        // Настоящий провайдер шаблонов — ловит опечатку в имени ресурса «meeting-report».
        var provider = new EmbeddedScribanPromptProvider(
            typeof(InspectorApplicationServiceCollectionExtensions).Assembly);

        var response = await new GenerateMeetingReportCommand.Handler(
                generator, reader, accessProvider, provider)
            .Handle(new GenerateMeetingReportCommand(10), CancellationToken.None);

        captured.ShouldNotBeNull();
        captured!.Role.ShouldBe(ModelRole.Draft);
        captured.TaskPrompt.ShouldNotBeNull();
        captured.TaskPrompt.ShouldContain("ПРОСРОЧЕНО: 1");               // факты — из блока кода,
        captured.TaskPrompt.ShouldContain("ничего не выдумывай");         // другие источники запрещены.
        captured.TaskPrompt.ShouldNotContain("{{");                       // подстановки отработали.

        response.Status.ShouldBeTrue();
        response.Data!.DraftText.ShouldBe("текст справки");
        response.Data.RequiresHumanReview.ShouldBeTrue();
        // Факты пришли из режимного документа: гриф справки — max(гриф ответа 0, гриф протокола 2).
        response.Data.ResultClassification.ShouldBe((short)2);
    }

    [Fact(DisplayName = "Недоступный по решётке протокол неотличим от несуществующего — NotFound")]
    public async Task Inaccessible_protocol_is_not_found()
    {
        var accessProvider = Substitute.For<IAccessContextProvider>();
        accessProvider.GetCurrentAsync(Arg.Any<CancellationToken>())
            .Returns(new AccessContext("u1", MaxClassification: 0, AllowedDivisions: []));

        var reader = Substitute.For<IMeetingProtocolReader>();
        reader.ReadAsync(99, Arg.Any<CancellationToken>()).Returns((MeetingProtocol?)null);

        var generator = Substitute.For<IGroundedGenerator>();
        var provider = new EmbeddedScribanPromptProvider(
            typeof(InspectorApplicationServiceCollectionExtensions).Assembly);

        var response = await new GenerateMeetingReportCommand.Handler(
                generator, reader, accessProvider, provider)
            .Handle(new GenerateMeetingReportCommand(99), CancellationToken.None);

        response.Status.ShouldBeFalse();
        response.StatusMessage.ShouldBe("Протокол не найден.");
        // До генерации дело не дошло — модель не звали.
        await generator.DidNotReceiveWithAnyArgs().GenerateAsync(default!, default!, default);
    }
}
