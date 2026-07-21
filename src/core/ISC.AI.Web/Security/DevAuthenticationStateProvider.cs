using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace ISC.AI.Web.Security;

/// <summary>
/// DEV-заглушка состояния аутентификации (пара к <see cref="DevAccessContextProvider"/>): всегда
/// «вошедший» псевдо-пользователь <c>dev</c>, чтобы каркас (AuthorizeView/AuthorizeRouteView) был
/// запускаем без слоя идентификации. Регистрируется ТОЛЬКО при <c>Auth:Mode=Dev</c> в Development.
/// </summary>
public sealed class DevAuthenticationStateProvider : AuthenticationStateProvider
{
    private static readonly Task<AuthenticationState> State = Task.FromResult(new AuthenticationState(
        new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "0"),
                new Claim(ClaimTypes.Name, "dev"),
            ],
            authenticationType: "Dev"))));

    /// <inheritdoc />
    public override Task<AuthenticationState> GetAuthenticationStateAsync() => State;
}
