using ISC.AI.AI.Grounding;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Grounding;
using Mediator;
using Shouldly;

namespace ISC.AI.UnitTests.Rag;

/// <summary>
/// Сквозное поведение грунтовки (Э4-19, §5.3.1.1, ТБ-041): грунтующие сценарии (<see cref="IGroundedScenario"/>)
/// не могут вернуть успех без вердикта грунтовки (обход запрещён — fail-closed), а непроверенные ссылки
/// помечаются на уровне конверта (вывод не «готов»).
/// </summary>
[Trait("Category", "Gate")]
public sealed class GroundingBehaviorTests
{
    private sealed record FakeCommand : IMessage, IGroundedScenario;

    private sealed record GroundedPayload(bool AllCitationsConfirmed) : IGroundedResult;

    private sealed record PlainPayload(string Text);

    private static ValueTask<ResponseDto<TPayload>> Run<TPayload>(ResponseDto<TPayload> handlerResult) =>
        new GroundingBehavior<FakeCommand, ResponseDto<TPayload>>().Handle(
            new FakeCommand(),
            (_, _) => ValueTask.FromResult(handlerResult),
            CancellationToken.None);

    [Fact(DisplayName = "Поведение грунтовки: непроверенные ссылки → пометка на конверте (не «готов»)")]
    public async Task Unconfirmed_result_is_flagged()
    {
        var result = await Run(ResponseDto<GroundedPayload>.Ok(new GroundedPayload(AllCitationsConfirmed: false)));

        result.Status.ShouldBeTrue(); // черновик произведён (HITL), но помечен как требующий проверки
        result.StatusMessage.ShouldContain("НЕподтверждённые");
    }

    [Fact(DisplayName = "Поведение грунтовки: все ссылки подтверждены → конверт без пометки")]
    public async Task Confirmed_result_passes_through()
    {
        var result = await Run(ResponseDto<GroundedPayload>.Ok(new GroundedPayload(AllCitationsConfirmed: true)));

        result.Status.ShouldBeTrue();
        result.StatusMessage.ShouldBe("Успешно");
    }

    [Fact(DisplayName = "Поведение грунтовки: грунтующий сценарий без вердикта → отказ (обход запрещён)")]
    public async Task Grounded_scenario_without_verdict_throws()
    {
        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await Run(ResponseDto<PlainPayload>.Ok(new PlainPayload("нет вердикта грунтовки"))));
    }

    [Fact(DisplayName = "Поведение грунтовки: неуспешный ответ пропускается без проверки вердикта")]
    public async Task Failed_response_passes_through()
    {
        var result = await Run(ResponseDto<PlainPayload>.Fail("ошибка входа"));

        result.Status.ShouldBeFalse();
        result.StatusMessage.ShouldBe("ошибка входа");
    }
}
