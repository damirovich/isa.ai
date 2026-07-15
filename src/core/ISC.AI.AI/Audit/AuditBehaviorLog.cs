using Microsoft.Extensions.Logging;

namespace ISC.AI.AI.Audit;

/// <summary>Строго-типизированные лог-сообщения сквозного аудита (LoggerMessage — CA1848).</summary>
internal static partial class AuditBehaviorLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Аудит «{Scenario}»: контекст доступа недоступен — гриф записи выставлен максимальным (fail-closed)")]
    public static partial void AccessUnavailable(ILogger logger, Exception exception, string scenario);
}
