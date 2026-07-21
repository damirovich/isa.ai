using Microsoft.Extensions.Logging;

namespace ISC.AI.Web.Security;

/// <summary>Строго-типизированные лог-сообщения входа (LoggerMessage — CA1848).</summary>
internal static partial class LoginServiceLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Вход отклонён: имя входа «{Login}» уже занято другой локальной учёткой (переиспользованный логин во внешней системе, автопривязка небезопасна)")]
    public static partial void UserNameConflict(ILogger logger, string login);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Проигранная гонка JIT-создания учётки при входе — перечитываем строку, вставленную параллельным запросом")]
    public static partial void ConcurrentJitInsertLost(ILogger logger, Exception exception);
}
