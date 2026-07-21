using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;

namespace ISC.AI.Web.Security;

/// <summary>
/// Ревалидация живых Blazor-сессий (ТБ-014/016): каждые <c>Auth:RevalidationMinutes</c> минут сверяет
/// штамп безопасности и блокировку во внешней системе идентификации и активность локального
/// пользователя; при расхождении circuit принудительно завершается (пользователь разлогинивается).
/// </summary>
/// <remarks>
/// Это страховка СЕССИИ на время, пока вкладка/circuit открыты. Вторая, независимая линия — cookie-
/// событие <c>OnValidatePrincipal</c> (см. <c>Program.cs</c>): она гасит саму cookie на HTTP-уровне,
/// поэтому перезагрузка страницы после ревалидации circuit'а не восстанавливает сессию (иначе F5 обходил
/// бы этот класс — вкладка просто открыла бы НОВЫЙ circuit со своим окном ревалидации). Обе линии
/// используют один <see cref="ExternalIdentityRevalidator"/>, чтобы решение не расходилось.
///
/// Немедленность отзыва ДОПУСКА (ТБ-016) обеспечивается отдельно и строже —
/// <see cref="ClearanceAccessContextProvider"/> читает допуск из БД на каждую операцию, поэтому даже
/// внутри окна ревалидации отозванный допуск уже не даёт извлечения.
/// </remarks>
public sealed class StampRevalidatingAuthenticationStateProvider(
    ILoggerFactory loggerFactory,
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration) : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    /// <inheritdoc />
    protected override TimeSpan RevalidationInterval =>
        TimeSpan.FromMinutes(
            int.TryParse(configuration["Auth:RevalidationMinutes"], out var minutes) && minutes > 0 ? minutes : 5);

    /// <inheritdoc />
    protected override async Task<bool> ValidateAuthenticationStateAsync(
        AuthenticationState authenticationState, CancellationToken cancellationToken)
    {
        var principal = authenticationState.User;
        if (principal.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        var externalId = principal.FindFirst(AuthClaims.ExternalId)?.Value;
        var stamp = principal.FindFirst(AuthClaims.SecurityStamp)?.Value;
        var idValue = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (externalId is null || stamp is null
            || !int.TryParse(idValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var userId))
        {
            return false;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var revalidator = scope.ServiceProvider.GetRequiredService<ExternalIdentityRevalidator>();
        var outcome = await revalidator.RevalidateAsync(userId, externalId, stamp, cancellationToken);

        // Unavailable ≠ Invalid: транзиентный сбой внешней системы НЕ обязан гасить живые сессии
        // (см. XML-doc RevalidationOutcome.Unavailable) — считаем сессию действующей до следующего тика.
        return outcome != RevalidationOutcome.Invalid;
    }
}
