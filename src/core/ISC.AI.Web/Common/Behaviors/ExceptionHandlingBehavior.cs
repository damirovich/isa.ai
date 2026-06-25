using FluentValidation;
using ISC.AI.Abstractions.Application;
using Mediator;

namespace ISC.AI.Web.Common.Behaviors;

/// <summary>
/// Внешнее (первое) сквозное поведение Mediator: оборачивает обработку в try-catch и превращает
/// исключения в НЕуспешный <see cref="IResponseDto"/>, чтобы сценарии не «роняли» приложение.
/// Ошибки валидации (<see cref="ValidationException"/>) — отдельно, с сообщениями правил.
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
