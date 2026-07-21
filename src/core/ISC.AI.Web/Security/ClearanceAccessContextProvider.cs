using System.Globalization;
using System.Security.Claims;
using ISC.AI.Abstractions.Security;
using ISC.AI.Persistence.Security;
using Microsoft.AspNetCore.Components.Authorization;

namespace ISC.AI.Web.Security;

/// <summary>
/// Боевой провайдер контекста доступа (Э3-08, ТБ-011/012): субъект — из аутентифицированной сессии,
/// допуск — из <c>core.clearance</c> ЧТЕНИЕМ НА КАЖДУЮ ОПЕРАЦИЮ (не из клеймов cookie).
/// </summary>
/// <remarks>
/// ИНВАРИАНТ БЕЗОПАСНОСТИ (ТД-004): допуск не кэшируется в сессии — отзыв действует для retrieval
/// немедленно (ТБ-016). FAIL-CLOSED (ТБ-012/021): нет аутентификации / нет пользователя / нет
/// действующего допуска — <see cref="AccessContextRequiredException"/>, а не «пустой» доступ.
/// Субъект берётся из состояния Blazor-circuit'а, вне circuit'а — из <see cref="HttpContext"/>.
/// </remarks>
public sealed class ClearanceAccessContextProvider(
    ClearanceAccessReader clearanceReader,
    AuthenticationStateProvider authenticationStateProvider,
    IHttpContextAccessor httpContextAccessor) : IAccessContextProvider
{
    /// <inheritdoc />
    public async Task<AccessContext> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        var principal = await ResolvePrincipalAsync();
        if (principal?.Identity?.IsAuthenticated != true)
        {
            throw new AccessContextRequiredException();
        }

        var idValue = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!int.TryParse(idValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var userId))
        {
            throw new AccessContextRequiredException();
        }

        // Свежее чтение допуска из БД — отзыв/деактивация видны немедленно (ТБ-016).
        return await clearanceReader.ReadAsync(userId, cancellationToken)
            ?? throw new AccessContextRequiredException();
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
