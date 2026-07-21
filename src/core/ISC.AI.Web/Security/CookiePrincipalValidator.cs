using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace ISC.AI.Web.Security;

/// <summary>
/// HTTP-уровневая ревалидация cookie-сессии (Э3-08, ТБ-014/016): <c>CookieAuthenticationEvents.OnValidatePrincipal</c>.
/// </summary>
/// <remarks>
/// Без этого блокировка/смена пароля во внешней системе не гасит УЖЕ ВЫДАННУЮ cookie — она остаётся
/// валидной для аутентификации до истечения (до 30 мин скользящего таймаута), и перезагрузка страницы
/// поднимает НОВЫЙ Blazor-circuit со своим окном ревалидации, продлевая доступ заблокированного
/// пользователя неограниченно. Здесь — вторая, независимая линия проверки на каждый HTTP-запрос
/// (не на каждый — раз в окно <c>Auth:RevalidationMinutes</c>, отслеживается клеймом <see cref="AuthClaims.ValidatedAt"/>),
/// использующая тот же <see cref="ExternalIdentityRevalidator"/>, что и ревалидация Blazor-circuit'а.
/// </remarks>
public static class CookiePrincipalValidator
{
    /// <summary>Обработчик <c>CookieAuthenticationOptions.Events.OnValidatePrincipal</c>.</summary>
    public static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        var principal = context.Principal;
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return;
        }

        var configuration = context.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
        var intervalMinutes = int.TryParse(configuration["Auth:RevalidationMinutes"], out var minutes) && minutes > 0
            ? minutes
            : 5;

        var validatedAtClaim = principal.FindFirst(AuthClaims.ValidatedAt)?.Value;
        if (validatedAtClaim is not null
            && long.TryParse(validatedAtClaim, NumberStyles.Integer, CultureInfo.InvariantCulture, out var validatedAtSeconds))
        {
            var validatedAt = DateTimeOffset.FromUnixTimeSeconds(validatedAtSeconds);
            if (DateTimeOffset.UtcNow - validatedAt < TimeSpan.FromMinutes(intervalMinutes))
            {
                // Окно ревалидации ещё не истекло — не бьём внешнюю систему на каждый HTTP-запрос.
                return;
            }
        }

        var externalId = principal.FindFirst(AuthClaims.ExternalId)?.Value;
        var stamp = principal.FindFirst(AuthClaims.SecurityStamp)?.Value;
        var idValue = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (externalId is null || stamp is null
            || !int.TryParse(idValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var userId))
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return;
        }

        var revalidator = context.HttpContext.RequestServices.GetRequiredService<ExternalIdentityRevalidator>();
        var outcome = await revalidator.RevalidateAsync(userId, externalId, stamp, context.HttpContext.RequestAborted);

        if (outcome == RevalidationOutcome.Invalid)
        {
            // Учётка заблокирована/штамп разошёлся/деактивирована — гасим САМУ cookie, не только
            // текущий circuit: следующая перезагрузка страницы уже не восстановит сессию (ТБ-016).
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return;
        }

        // Valid или Unavailable (транзиентный сбой — не гасим сессию, см. RevalidationOutcome.Unavailable):
        // в обоих случаях продлеваем окно, чтобы не долбить внешнюю систему на каждый следующий запрос.
        var identity = (ClaimsIdentity)principal.Identity;
        var oldStamp = identity.FindFirst(AuthClaims.ValidatedAt);
        if (oldStamp is not null)
        {
            identity.RemoveClaim(oldStamp);
        }

        identity.AddClaim(new Claim(
            AuthClaims.ValidatedAt, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)));
        context.ShouldRenew = true;
        context.ReplacePrincipal(principal);
    }
}
