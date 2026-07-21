using Microsoft.Extensions.Logging;

namespace ISC.AI.Web.Security;

/// <summary>Строго-типизированные лог-сообщения эндпоинтов входа/выхода (LoggerMessage — CA1848).</summary>
internal static partial class AuthEndpointsLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Вход невозможен: внешняя система идентичности недоступна")]
    public static partial void IdentityProviderUnavailable(ILogger logger, Exception exception);
}
