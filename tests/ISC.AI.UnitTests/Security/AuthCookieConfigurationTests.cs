using ISC.AI.Web.Security;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Shouldly;

namespace ISC.AI.UnitTests.Security;

/// <summary>
/// Конфигурационный аудит ТБ-010: режимная cookie аутентификации должна выставлять флаг Secure ВСЕГДА
/// (не SameAsRequest) — за TLS-терминирующим прокси бэкенд видит запрос как HTTP, и SameAsRequest не
/// проставил бы Secure, отдав cookie в открытом виде. Плюс HttpOnly и SameSite.
/// </summary>
public sealed class AuthCookieConfigurationTests
{
    [Fact(DisplayName = "ТБ-010: в проде cookie — Secure ВСЕГДА (Always), HttpOnly, SameSite=Lax")]
    public void Prod_cookie_is_hardened()
    {
        var options = new CookieAuthenticationOptions();

        AuthCookieConfiguration.Configure(options, idleMinutes: 30, isDevelopment: false);

        options.Cookie.SecurePolicy.ShouldBe(CookieSecurePolicy.Always); // ТБ-010: не SameAsRequest
        options.Cookie.HttpOnly.ShouldBeTrue();
        options.Cookie.SameSite.ShouldBe(SameSiteMode.Lax);
        options.Cookie.Name.ShouldBe(".ISC.AI.Auth");
        options.ExpireTimeSpan.ShouldBe(TimeSpan.FromMinutes(30)); // idle-таймаут (ТБ-014)
    }

    [Fact(DisplayName = "Dev: cookie — Secure=SameAsRequest (иначе вход по http://localhost сломался бы)")]
    public void Dev_cookie_allows_http_localhost()
    {
        var options = new CookieAuthenticationOptions();

        AuthCookieConfiguration.Configure(options, idleMinutes: 30, isDevelopment: true);

        options.Cookie.SecurePolicy.ShouldBe(CookieSecurePolicy.SameAsRequest);
        options.Cookie.HttpOnly.ShouldBeTrue(); // прочие защиты — те же, что в проде
    }
}
