using System.Globalization;
using System.Security.Claims;
using ISC.AI.Abstractions.Security;
using Microsoft.AspNetCore.Components.Authorization;

namespace ISC.AI.Web.Security;

/// <summary>
/// Кто вошёл — из аутентифицированной сессии (Э3-08). Реализация <see cref="ISubjectProvider"/>:
/// ТОЛЬКО идентификация, без обращения к допуску.
/// </summary>
/// <remarks>
/// Субъект берётся из состояния Blazor-circuit'а, а вне circuit'а (обычный HTTP-запрос) — из
/// <see cref="HttpContext"/>; та же схема, что у <see cref="ClearanceAccessContextProvider"/>,
/// который теперь опирается на этот же порт, чтобы разбор принципала не разъехался по двум копиям.
/// </remarks>
public sealed class HttpSubjectProvider(
    AuthenticationStateProvider authenticationStateProvider,
    IHttpContextAccessor httpContextAccessor) : ISubjectProvider
{
    /// <inheritdoc />
    public async Task<int?> GetCurrentUserIdAsync(CancellationToken cancellationToken = default)
    {
        var principal = await ResolvePrincipalAsync();
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var idValue = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        // Ноль отбрасывается намеренно: dev-заглушка входа выдаёт NameIdentifier="0" —
        // это не пользователь реестра, и администрировать от его имени нечего.
        return int.TryParse(idValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var userId)
            && userId > 0
                ? userId
                : null;
    }

    private async Task<ClaimsPrincipal?> ResolvePrincipalAsync()
    {
        try
        {
            var state = await authenticationStateProvider.GetAuthenticationStateAsync();
            return state.User;
        }
        catch (InvalidOperationException)
        {
            // Вне Blazor-circuit'а (обычный HTTP-запрос) состояние circuit'а недоступно — берём принципала запроса.
            return httpContextAccessor.HttpContext?.User;
        }
    }
}
