using FluentValidation;
using FluentValidation.Results;
using ISC.AI.Abstractions.AI;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Enums;
using ISC.AI.Web.Common.Behaviors;
using Mediator;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace ISC.AI.UnitTests.Web;

/// <summary>
/// Внешнее поведение конвейера превращает исключения сценария в НЕуспешный конверт ответа, не роняя
/// приложение. Ключевой инвариант ТН-003/ТНД-001: недоступность модели (<see cref="ModelUnavailableException"/>)
/// ловится ОТДЕЛЬНО как управляемая деградация (<see cref="ResponseStatusCode.ServiceUnavailable"/> + честное
/// «повторите позже»), а не сливается с обезличенной внутренней ошибкой.
/// </summary>
public sealed class ExceptionHandlingBehaviorTests
{
    private sealed record FakeRequest : IMessage;

    private static ExceptionHandlingBehavior<FakeRequest, ResponseDto<string>> Build() =>
        new(NullLogger<ExceptionHandlingBehavior<FakeRequest, ResponseDto<string>>>.Instance);

    private static ValueTask<ResponseDto<string>> Handle(
        ExceptionHandlingBehavior<FakeRequest, ResponseDto<string>> behavior,
        MessageHandlerDelegate<FakeRequest, ResponseDto<string>> next) =>
        behavior.Handle(new FakeRequest(), next, CancellationToken.None);

    [Fact(DisplayName = "Деградация: недоступность модели → ServiceUnavailable + честное «повторите позже», не Internal")]
    public async Task Model_unavailable_maps_to_service_unavailable()
    {
        var behavior = Build();

        var response = await Handle(
            behavior, (_, _) => throw new ModelUnavailableException(ModelRole.Draft, innerException: null));

        response.Status.ShouldBeFalse();
        response.StatusCode.ShouldBe(ResponseStatusCode.ServiceUnavailable);
        response.StatusMessage.ShouldContain("временно недоступен");
        response.StatusMessage.ShouldNotContain("Внутренняя ошибка"); // не обезличенный дефект
    }

    [Fact(DisplayName = "Прочее исключение → InternalServerError (обезличенная внутренняя ошибка)")]
    public async Task Generic_exception_maps_to_internal_error()
    {
        var behavior = Build();

        var response = await Handle(
            behavior, (_, _) => throw new InvalidOperationException("что-то пошло не так"));

        response.Status.ShouldBeFalse();
        response.StatusCode.ShouldBe(ResponseStatusCode.InternalServerError);
    }

    [Fact(DisplayName = "Ошибка валидации → ValidationError с сообщениями правил")]
    public async Task Validation_exception_maps_to_validation_error()
    {
        var behavior = Build();

        var response = await Handle(
            behavior, (_, _) => throw new ValidationException([new ValidationFailure("Тема", "Тема обязательна")]));

        response.Status.ShouldBeFalse();
        response.StatusCode.ShouldBe(ResponseStatusCode.ValidationError);
        response.StatusMessage.ShouldContain("Тема обязательна");
    }

    [Fact(DisplayName = "Успешный сценарий проходит насквозь без изменений")]
    public async Task Success_passes_through()
    {
        var behavior = Build();

        var response = await Handle(behavior, (_, _) => ValueTask.FromResult(ResponseDto<string>.Ok("готово")));

        response.Status.ShouldBeTrue();
        response.StatusCode.ShouldBe(ResponseStatusCode.Ok);
    }
}
