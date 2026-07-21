using ISC.AI.Abstractions.Security;
using ISC.AI.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Web.Security;

/// <summary>Итог проверки живой сессии против внешней системы идентичности (ТБ-014/016).</summary>
public enum RevalidationOutcome
{
    /// <summary>Учётка действительна: штамп совпал, не заблокирована, локально активна.</summary>
    Valid,

    /// <summary>Учётка НЕДЕЙСТВИТЕЛЬНА (заблокирована/штамп разошёлся/деактивирована локально) — разлогинить.</summary>
    Invalid,

    /// <summary>
    /// Внешняя система временно недоступна (сеть/БД) — это НЕ решение «учётка недействительна».
    /// Сессия остаётся действующей до следующей попытки; не путать с Invalid (иначе кратковременный
    /// сбой БД СКИД массово разлогинивает всех живых пользователей — недокументированное и неверное
    /// поведение). Реальный контроль доступа не ослабляется: допуск читается из БД на каждую операцию
    /// независимо (<see cref="ClearanceAccessContextProvider"/>), fail-closed сам по себе.
    /// </summary>
    Unavailable,
}

/// <summary>
/// Общая логика периодической проверки живой сессии (Э3-08, ТБ-014/016): используется и cookie-событием
/// <c>OnValidatePrincipal</c> (HTTP-уровень — гасит саму cookie), и <see cref="StampRevalidatingAuthenticationStateProvider"/>
/// (Blazor-circuit — быстрее реагирует, пока вкладка открыта). Один источник правды для обоих путей.
/// </summary>
public sealed class ExternalIdentityRevalidator(
    IExternalIdentityProvider identityProvider,
    IDbContextFactory<CoreDbContext> contextFactory,
    ILogger<ExternalIdentityRevalidator> logger)
{
    /// <summary>
    /// Сверяет штамп безопасности/блокировку во внешней системе и активность локального субъекта.
    /// Транзиентная ошибка обращения к внешней системе не приравнивается к «недействителен» (см.
    /// <see cref="RevalidationOutcome.Unavailable"/>) — иначе сбой сети/БД СКИД массово гасит сессии.
    /// </summary>
    public async Task<RevalidationOutcome> RevalidateAsync(
        int userId, string externalId, string stamp, CancellationToken cancellationToken)
    {
        ExternalIdentityState? state;
        try
        {
            state = await identityProvider.GetStateAsync(externalId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ExternalIdentityRevalidatorLog.ProviderUnavailable(logger, ex);
            return RevalidationOutcome.Unavailable;
        }

        if (state is null || state.IsBlocked || !string.Equals(state.SecurityStamp, stamp, StringComparison.Ordinal))
        {
            return RevalidationOutcome.Invalid;
        }

        // Локальный субъект: не деактивирован (мягкое удаление скрыто глобальным фильтром). Это наша
        // собственная БД — недоступность здесь не транзиентная относительно остального приложения
        // (без неё вообще ничего не работает), поэтому исключение не перехватывается отдельно.
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var isActive = await db.Users.AnyAsync(u => u.Id == userId && u.IsActive, cancellationToken);
        return isActive ? RevalidationOutcome.Valid : RevalidationOutcome.Invalid;
    }
}

/// <summary>Строго-типизированные лог-сообщения ревалидации (LoggerMessage — CA1848).</summary>
internal static partial class ExternalIdentityRevalidatorLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Ревалидация сессии: внешняя система идентичности недоступна — сессия НЕ гасится (транзиентный сбой ≠ отзыв), реальный контроль доступа не ослаблен")]
    public static partial void ProviderUnavailable(ILogger logger, Exception exception);
}
