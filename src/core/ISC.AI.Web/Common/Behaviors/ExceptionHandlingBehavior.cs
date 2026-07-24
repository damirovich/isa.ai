using FluentValidation;
using ISC.AI.Abstractions.AI;
using ISC.AI.Abstractions.Application;
using Mediator;

namespace ISC.AI.Web.Common.Behaviors;

/// <summary>
/// Внешнее (первое) сквозное поведение Mediator: оборачивает обработку в try-catch и превращает
/// исключения в НЕуспешный <see cref="IResponseDto"/>, чтобы сценарии не «роняли» приложение.
/// Ошибки валидации (<see cref="ValidationException"/>) — отдельно, с сообщениями правил.
/// Недоступность модели (<see cref="ModelUnavailableException"/>) — отдельно, как управляемая деградация
/// с честным сообщением «повторите позже» (ТН-003/ТНД-001), а не обезличенная внутренняя ошибка.
/// Применяется только к сообщениям, чей ответ — <see cref="IResponseDto"/> (общий конверт).
/// </summary>
public sealed class ExceptionHandlingBehavior<TMessage, TResponse>(
    ILogger<ExceptionHandlingBehavior<TMessage, TResponse>> logger)
    : IPipelineBehavior<TMessage, TResponse>
    where TMessage : notnull, IMessage
    where TResponse : IResponseDto, new()
{
    /// <inheritdoc />
    public async ValueTask<TResponse> Handle(
        TMessage message, MessageHandlerDelegate<TMessage, TResponse> next, CancellationToken cancellationToken)
    {
        try
        {
            return await next(message, cancellationToken);
        }
        catch (ValidationException validationException)
        {
            return new TResponse
            {
                Status = false,
                StatusCode = ResponseStatusCode.ValidationError,
                StatusMessage = string.Join("; ", validationException.Errors.Select(e => e.ErrorMessage)),
            };
        }
        catch (ModelUnavailableException modelUnavailable)
        {
            // Управляемая деградация (ТН-003/ТНД-001): сервер инференса временно недоступен — это ожидаемое
            // временное состояние, а не дефект. Ловим ОТДЕЛЬНО от generic-ветки (инвариант
            // ModelUnavailableException) и отдаём честное «повторите позже», а не обезличенную внутреннюю ошибку.
            BehaviorLog.ModelUnavailable(logger, modelUnavailable, typeof(TMessage).Name);
            return new TResponse
            {
                Status = false,
                StatusCode = ResponseStatusCode.ServiceUnavailable,
                StatusMessage = "Сервер модели временно недоступен. Повторите попытку позже.",
            };
        }
        catch (Exception exception)
        {
            var name = typeof(TMessage).Name;
            BehaviorLog.UnhandledError(logger, exception, name);
            return new TResponse
            {
                Status = false,
                StatusCode = ResponseStatusCode.InternalServerError,
                StatusMessage = $"Внутренняя ошибка при выполнении «{name}».",
            };
        }
    }
}
