using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;

namespace ISC.AI.Web.Security;

/// <summary>
/// Режимные параметры cookie аутентификации (ТБ-010/014). Вынесено из <c>Program.cs</c>, чтобы
/// критичные для безопасности настройки можно было проверить тестом (конфигурационный аудит ТБ-010).
/// </summary>
internal static class AuthCookieConfiguration
{
    /// <summary>
    /// Настраивает cookie схемы аутентификации: имя, HttpOnly, SameSite, Secure (по среде), idle-таймаут,
    /// ревалидация. <paramref name="isDevelopment"/> = <see langword="true"/> — dev по http localhost
    /// (Secure=SameAsRequest, иначе браузер не вернёт cookie по http и вход сломается); прод — Secure=Always.
    /// </summary>
    public static void Configure(CookieAuthenticationOptions options, int idleMinutes, bool isDevelopment)
    {
        options.LoginPath = "/login";
        options.AccessDeniedPath = "/access-denied";
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromMinutes(idleMinutes);
        options.Cookie.Name = ".ISC.AI.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;

        // ПРОД: ВСЕГДА Secure (ТБ-010): в контуре HTTPS обязателен. За TLS-терминирующим прокси бэкенд видит
        // запрос как HTTP (X-Forwarded-Proto обрабатывается только при заданном KnownProxies), поэтому
        // SameAsRequest НЕ проставил бы флаг Secure — режимная cookie ушла бы в открытом виде. Always —
        // безусловно, независимо от схемы бэкенд-хопа.
        // DEV: SameAsRequest — локальный запуск по http://localhost не режим; Always там сломал бы вход
        // (Secure-cookie не шлётся по http).
        options.Cookie.SecurePolicy = isDevelopment ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;

        // Вторая, независимая от Blazor-circuit'а линия ревалидации (ТБ-014/016): без неё блокировка/смена
        // пароля во внешней системе не гасит уже выданную cookie — перезагрузка страницы поднимает новый
        // circuit со своим окном ревалидации, продлевая доступ заблокированного пользователя.
        options.Events.OnValidatePrincipal = CookiePrincipalValidator.ValidateAsync;
    }
}
