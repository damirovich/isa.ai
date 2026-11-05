using System.Globalization;
using System.Security.Claims;
using ISC.AI.Abstractions.Security;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;

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
    IHttpContextAccessor httpContextAccessor,
    IUserSessionStore sessionStore,
    CircuitClientAddress circuitAddress) : ISubjectProvider
{
    // Адрес входа в сеанс не меняется — один запрос к базе на соединение, а не на каждую запись журнала. Ссылка на
    // запись, а не пара значений: присваивание ссылки атомарно и при параллельных записях журнала.
    private SessionAddress? _sessionAddress;
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

    /// <inheritdoc />
    public async Task<Guid?> GetCurrentSessionIdAsync(CancellationToken cancellationToken = default)
    {
        var principal = await ResolvePrincipalAsync();
        return principal?.Identity?.IsAuthenticated == true ? UserSessionTracker.SessionIdOf(principal) : null;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Адрес соединения, через которое пришло действие (ТБ-080, ADR-0066), по порядку: соединение Blazor — адрес,
    /// захваченный при его установлении или переподключении (<see cref="CircuitClientAddress"/>); HTTP-запрос (вход,
    /// скачивание файлов) — адрес запроса, за доверенным прокси — из X-Forwarded-For; иначе — адрес, захваченный при
    /// входе в сеанс (транспорт соединения адреса не дал).
    /// </remarks>
    public async Task<string?> GetCurrentClientAddressAsync(CancellationToken cancellationToken = default)
    {
        if (circuitAddress.Value is { } connection)
        {
            return connection;
        }

        if (ClientAddresses.Format(httpContextAccessor.HttpContext?.Connection.RemoteIpAddress) is { } request)
        {
            return request;
        }

        if (await GetCurrentSessionIdAsync(cancellationToken) is not { } sessionId)
        {
            return null;
        }

        if (_sessionAddress is { } cached && cached.SessionId == sessionId)
        {
            return cached.Address;
        }

        var address = await sessionStore.GetClientAddressAsync(sessionId, cancellationToken);
        _sessionAddress = new SessionAddress(sessionId, address);
        return address;
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

    private sealed record SessionAddress(Guid SessionId, string? Address);
}
