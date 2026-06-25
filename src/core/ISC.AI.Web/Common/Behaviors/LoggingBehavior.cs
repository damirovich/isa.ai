using System.Diagnostics;
using Mediator;

namespace ISC.AI.Web.Common.Behaviors;

/// <summary>
/// Сквозное поведение Mediator: логирует начало/конец обработки и время выполнения сценария (Serilog).
/// Нейтрально к типу сообщения.
/// </summary>
public sealed class LoggingBehavior<TMessage, TResponse>(ILogger<LoggingBehavior<TMessage, TResponse>> logger)
    : IPipelineBehavior<TMessage, TResponse>
    where TMessage : notnull, IMessage
{
    /// <inheritdoc />
    public async ValueTask<TResponse> Handle(
        TMessage message, MessageHandlerDelegate<TMessage, TResponse> next, CancellationToken cancellationToken)
    {
        var name = typeof(TMessage).Name;
        var timer = Stopwatch.StartNew();
        BehaviorLog.RequestStarted(logger, name);

        var response = await next(message, cancellationToken);

        timer.Stop();
        BehaviorLog.RequestFinished(logger, name, timer.ElapsedMilliseconds);
        return response;
    }
}
