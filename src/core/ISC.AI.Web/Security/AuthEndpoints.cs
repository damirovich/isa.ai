using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace ISC.AI.Web.Security;

/// <summary>
/// HTTP-эндпоинты входа/выхода (Э3-08, ТБ-010/014). Форма входа постится обычным POST (вне
/// Blazor-circuit'а) — только так cookie сессии устанавливается штатно; антифорджери-токен
/// проверяется автоматически (форма несёт <c>AntiforgeryToken</c>).
/// </summary>
public static class AuthEndpoints
{
    /// <summary>Имя политики троттлинга входа (защита от перебора паролей).</summary>
    public const string LoginRateLimitPolicy = "auth-login";

    /// <summary>Маршруты <c>POST /auth/login</c> и <c>POST /auth/logout</c>.</summary>
    public static void MapAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/auth/login", async (
                HttpContext http,
                [FromForm(Name = "login")] string login,
                [FromForm(Name = "password")] string password,
                [FromForm(Name = "returnUrl")] string? returnUrl,
                LoginService loginService,
                ILogger<LoginService> logger,
                CancellationToken cancellationToken) =>
            {
                System.Security.Claims.ClaimsPrincipal? principal;
                try
                {
                    principal = await loginService.AuthenticateAsync(login, password, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Внешняя система идентичности недоступна (сеть/БД СКИД) — операционная ошибка,
                    // не «неверные данные»: не аудируем как попытку входа (проверка учётных данных не
                    // состоялась), отдаём отдельное сообщение вместо голой страницы /Error (ТН-003).
                    AuthEndpointsLog.IdentityProviderUnavailable(logger, ex);
                    return Results.Redirect(
                        $"/login?error=unavailable&returnUrl={Uri.EscapeDataString(SafeLocalUrl(returnUrl))}");
                }

                if (principal is null)
                {
                    // Единый отказ без различения причин; returnUrl сохраняется для повторной попытки.
                    return Results.Redirect($"/login?error=1&returnUrl={Uri.EscapeDataString(SafeLocalUrl(returnUrl))}");
                }

                await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
                return Results.Redirect(SafeLocalUrl(returnUrl));
            })
            .AllowAnonymous()
            .RequireRateLimiting(LoginRateLimitPolicy);

        // IFormCollection форсирует ту же антифорджери-метадату, что уже действует на /auth/login
        // (minimal API включает проверку токена автоматически только для form-bound эндпоинтов) —
        // без параметра формы форма выхода отправлялась бы БЕЗ проверки антифорджери-токена.
        endpoints.MapPost("/auth/logout", async (HttpContext http, IFormCollection _) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/login");
        });
    }

    // Только локальные пути — защита от open-redirect через returnUrl. Отвергает не только "//host"
    // (protocol-relative), но и "/\host" — браузер трактует обратный слеш как разделитель пути и
    // резолвит такую "локальную" ссылку во внешний хост (WHATWG URL); Url.IsLocalUrl отвергает оба.
    // internal — покрыто регрессионным тестом на обход "/\host" (см. ISC.AI.UnitTests).
    internal static string SafeLocalUrl(string? url) =>
        !string.IsNullOrEmpty(url) && url.StartsWith('/') && url.Length > 1 && url[1] is not ('/' or '\\')
            ? url
            : "/";
}
