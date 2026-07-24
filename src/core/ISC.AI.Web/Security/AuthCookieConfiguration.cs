using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;

namespace ISC.AI.Web.Security;

/// <summary>
/// Режимные параметры cookie аутентификации (ТБ-010/014). Вынесено из <c>Program.cs</c>, чтобы
/// критичные для безопасности настройки можно было проверить тестом (конфигурационный аудит ТБ-010).
/// </summary>
internal static class AuthCookieConfiguration
{
    /// <summary>Настраивает cookie схемы аутентификации: имя, HttpOnly, SameSite, Secure=Always, idle-таймаут, ревалидация.</summary>
    public static void Configure(CookieAuthenticationOptions options, int idleMinutes)
    {
        options.LoginPath = "/login";
        options.AccessDeniedPath = "/access-denied";
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromMinutes(idleMinutes);
        options.Cookie.Name = ".ISC.AI.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;

        // ВСЕГДА Secure (ТБ-010): в контуре HTTPS обязателен. За TLS-терминирующим прокси бэкенд видит
        // запрос как HTTP (X-Forwarded-Proto обрабатывается только при заданном KnownProxies), поэтому
        // SameAsRequest НЕ проставил бы флаг Secure — режимная cookie ушла бы в открытом виде. Always —
        // безусловно, независимо от схемы бэкенд-хопа.
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;

        // Вторая, независимая от Blazor-circuit'а линия ревалидации (ТБ-014/016): без неё блокировка/смена
        // пароля во внешней системе не гасит уже выданную cookie — перезагрузка страницы поднимает новый
        // circuit со своим окном ревалидации, продлевая доступ заблокированного пользователя.
        options.Events.OnValidatePrincipal = CookiePrincipalValidator.ValidateAsync;
    }
}
