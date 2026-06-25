using FluentValidation;
using Mediator;

namespace ISC.AI.Web.Common.Behaviors;

/// <summary>
/// Сквозное поведение Mediator: прогоняет зарегистрированные <see cref="IValidator{T}"/> для сообщения
/// перед хендлером. При нарушениях — <see cref="ValidationException"/> (её ловит ExceptionHandlingBehavior
/// и превращает в неуспешный <c>ResponseDto</c>). Хендлеры остаются без ручных проверок.
/// </summary>
public sealed class ValidationBehavior<TMessage, TResponse>(IEnumerable<IValidator<TMessage>> validators)
    : IPipelineBehavior<TMessage, TResponse>
    where TMessage : notnull, IMessage
{
    /// <inheritdoc />
    public async ValueTask<TResponse> Handle(
        TMessage message, MessageHandlerDelegate<TMessage, TResponse> next, CancellationToken cancellationToken)
    {
        if (validators.Any())
        {
            var context = new ValidationContext<TMessage>(message);
            var failures = validators
                .Select(validator => validator.Validate(context))
                .SelectMany(result => result.Errors)
                .Where(failure => failure is not null)
                .ToList();

            if (failures.Count != 0)
            {
                throw new ValidationException(failures);
            }
        }

        return await next(message, cancellationToken);
    }
}
