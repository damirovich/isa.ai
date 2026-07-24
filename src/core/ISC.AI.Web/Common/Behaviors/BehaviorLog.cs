using Microsoft.Extensions.Logging;

namespace ISC.AI.Web.Common.Behaviors;

/// <summary>
/// Высокопроизводительные логи конвейера Mediator через source-generated <c>LoggerMessage</c>
/// (CA1848): сообщения не форматируются, если уровень логирования выключен.
/// </summary>
internal static partial class BehaviorLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "Mediator: начало {Request}")]
    public static partial void RequestStarted(ILogger logger, string request);

    [LoggerMessage(Level = LogLevel.Information, Message = "Mediator: конец {Request} за {ElapsedMs} мс")]
    public static partial void RequestFinished(ILogger logger, string request, long elapsedMs);

    [LoggerMessage(Level = LogLevel.Error, Message = "Mediator: необработанная ошибка в {Request}")]
    public static partial void UnhandledError(ILogger logger, Exception exception, string request);

    // Управляемая деградация (ТН-003/ТНД-001): недоступность модели — ожидаемое временное состояние,
    // не дефект приложения, поэтому Warning, а не Error, и БЕЗ полного стека в проде.
    [LoggerMessage(Level = LogLevel.Warning, Message = "Mediator: модель недоступна при выполнении {Request}")]
    public static partial void ModelUnavailable(ILogger logger, Exception exception, string request);
}
