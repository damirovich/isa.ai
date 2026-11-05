using System.Net;
using System.Security.Claims;
using ISC.AI.Abstractions.Security;
using ISC.AI.Web.Security;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;

namespace ISC.AI.UnitTests.Security;

/// <summary>
/// Адрес клиента в контексте субъекта (ТБ-080, ADR-0066): адрес соединения Blazor, иначе адрес HTTP-запроса, иначе —
/// адрес входа в сеанс; одна форма записи адреса для журнала и списка сеансов.
/// </summary>
public sealed class HttpSubjectProviderTests
{
    private static readonly Guid Session = Guid.Parse("4f1c2b7e-0d3a-4c55-9e21-7a6b5c4d3e2f");

    private readonly AuthenticationStateProvider _authentication = Substitute.For<AuthenticationStateProvider>();
    private readonly HttpContextAccessor _http = new();
    private readonly IUserSessionStore _sessions = Substitute.For<IUserSessionStore>();
    private readonly CircuitClientAddress _circuit = new();

    private HttpSubjectProvider Provider() => new(_authentication, _http, _sessions, _circuit);

    private void SignedIn(Guid? sessionId)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "7") };
        if (sessionId is { } id)
        {
            claims.Add(new Claim(UserSessionClaims.SessionId, id.ToString("D")));
        }

        _authentication.GetAuthenticationStateAsync()
            .Returns(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(claims, "test"))));
    }

    private static DefaultHttpContext Request(string address) =>
        new() { Connection = { RemoteIpAddress = IPAddress.Parse(address) } };

    [Fact(DisplayName = "ТБ-080: действие в соединении Blazor — адрес, захваченный при установлении соединения")]
    public async Task Circuit_address_comes_first()
    {
        SignedIn(Session);
        _circuit.Capture("10.1.1.1");
        _http.HttpContext = Request("10.2.2.2");

        (await Provider().GetCurrentClientAddressAsync()).ShouldBe("10.1.1.1");
        await _sessions.DidNotReceiveWithAnyArgs().GetClientAddressAsync(default);
    }

    [Fact(DisplayName = "ТБ-080: HTTP-запрос — адрес запроса; IPv4, отображённый в IPv6, записывается как IPv4")]
    public async Task Request_address_is_normalized()
    {
        _authentication.GetAuthenticationStateAsync().Throws(new InvalidOperationException("вне соединения"));
        _http.HttpContext = Request("::ffff:10.0.0.5");

        (await Provider().GetCurrentClientAddressAsync()).ShouldBe("10.0.0.5");
    }

    [Fact(DisplayName = "ТБ-080: соединение адреса не дало — адрес входа в сеанс, из хранилища один раз")]
    public async Task Falls_back_to_the_session_address()
    {
        SignedIn(Session);
        _sessions.GetClientAddressAsync(Session, Arg.Any<CancellationToken>()).Returns("10.9.9.9");
        var provider = Provider();

        (await provider.GetCurrentClientAddressAsync()).ShouldBe("10.9.9.9");
        (await provider.GetCurrentClientAddressAsync()).ShouldBe("10.9.9.9");
        await _sessions.Received(1).GetClientAddressAsync(Session, Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "ТБ-080: без соединения и без сеанса (фоновая работа) адрес неизвестен")]
    public async Task No_address_without_connection_and_session()
    {
        SignedIn(sessionId: null);

        (await Provider().GetCurrentClientAddressAsync()).ShouldBeNull();
        await _sessions.DidNotReceiveWithAnyArgs().GetClientAddressAsync(default);
    }

    [Fact(DisplayName = "ТБ-080: пустой адрес переподключения не затирает известный адрес соединения")]
    public void Empty_capture_keeps_the_known_address()
    {
        _circuit.Capture("10.1.1.1");
        _circuit.Capture(null);
        _circuit.Capture(" ");

        _circuit.Value.ShouldBe("10.1.1.1");
    }

    [Fact(DisplayName = "ТБ-080: форма адреса — IPv4 как есть, IPv6 как есть, IPv4 в IPv6 — как IPv4, нет адреса — пусто")]
    public void Address_format()
    {
        ClientAddresses.Format(IPAddress.Parse("10.0.0.5")).ShouldBe("10.0.0.5");
        ClientAddresses.Format(IPAddress.Parse("::ffff:192.168.1.15")).ShouldBe("192.168.1.15");
        ClientAddresses.Format(IPAddress.Parse("fe80::1")).ShouldBe("fe80::1");
        ClientAddresses.Format(null).ShouldBeNull();
    }
}
