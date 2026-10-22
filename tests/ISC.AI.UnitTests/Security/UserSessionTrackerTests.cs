using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Security;
using ISC.AI.Web.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Security;

/// <summary>
/// Сеансы входа в хосте (ТФ-АДМ-09, ADR-0060): кэш «сеанс действует» не дольше 15 с, завершённый не оживает,
/// активность — в базу не чаще раза в минуту и без падения при сбое, мгновенный выход соединений завершённого сеанса,
/// «на связи» по соединениям; проверка cookie на каждом запросе отклоняет cookie без сеанса и с завершённым сеансом.
/// </summary>
public sealed class UserSessionTrackerTests
{
    private static readonly Guid Session = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid OtherSession = Guid.Parse("55555555-5555-5555-5555-555555555555");

    private readonly IUserSessionStore _store = Substitute.For<IUserSessionStore>();
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 10, 22, 9, 0, 0, TimeSpan.Zero));
    private readonly UserSessionTracker _tracker;

    public UserSessionTrackerTests()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Auth:SessionIdleMinutes"] = "20" })
            .Build();
        _tracker = new UserSessionTracker(_store, configuration, _clock, NullLogger<UserSessionTracker>.Instance);
        _store.IsActiveAsync(Session, Arg.Any<CancellationToken>()).Returns(true);
    }

    [Fact(DisplayName = "«Сеанс действует» кэшируется на 15 с; после — новый вопрос к базе; таймаут неактивности — из настройки")]
    public async Task Status_is_cached_briefly()
    {
        (await _tracker.IsActiveAsync(Session, CancellationToken.None)).ShouldBeTrue();
        _clock.Advance(TimeSpan.FromSeconds(10));
        (await _tracker.IsActiveAsync(Session, CancellationToken.None)).ShouldBeTrue();
        await _store.Received(1).IsActiveAsync(Session, Arg.Any<CancellationToken>());

        _clock.Advance(TimeSpan.FromSeconds(6));
        await _tracker.IsActiveAsync(Session, CancellationToken.None);
        await _store.Received(2).IsActiveAsync(Session, Arg.Any<CancellationToken>());

        _tracker.IdleTimeout.ShouldBe(TimeSpan.FromMinutes(20));
    }

    [Fact(DisplayName = "Завершённый сеанс не оживает: «нет» кэшируется бессрочно; сигнал завершения сбрасывает «да» сразу")]
    public async Task Ended_session_stays_ended()
    {
        (await _tracker.IsActiveAsync(Session, CancellationToken.None)).ShouldBeTrue();

        _tracker.SignalEnded(Session);

        (await _tracker.IsActiveAsync(Session, CancellationToken.None)).ShouldBeFalse();
        _clock.Advance(TimeSpan.FromHours(1));
        (await _tracker.IsActiveAsync(Session, CancellationToken.None)).ShouldBeFalse();
        await _store.Received(1).IsActiveAsync(Session, Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Активность — в базу не чаще раза в минуту; сбой записи не роняет запрос")]
    public async Task Activity_is_throttled_and_safe()
    {
        await _tracker.NoteActivityAsync(Session, CancellationToken.None);
        _clock.Advance(TimeSpan.FromSeconds(30));
        await _tracker.NoteActivityAsync(Session, CancellationToken.None);
        await _store.Received(1).TouchAsync(Session, Arg.Any<DateTime>(), Arg.Any<CancellationToken>());

        _clock.Advance(TimeSpan.FromSeconds(31));
        _store.TouchAsync(Session, Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("БД недоступна"));
        await Should.NotThrowAsync(() => _tracker.NoteActivityAsync(Session, CancellationToken.None));
        await _store.Received(2).TouchAsync(Session, Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Завершение: соединения ЭТОГО сеанса сразу переводятся в «не вошёл», чужие — нет; «на связи» — по связи соединения")]
    public void Signal_reaches_only_its_circuits()
    {
        var signedOut = new List<string>();
        object first = new(), second = new(), foreign = new();
        _tracker.RegisterCircuit(first, Session, () => signedOut.Add("first"));
        _tracker.RegisterCircuit(second, Session, () => throw new InvalidOperationException("соединение сломано"));
        _tracker.RegisterCircuit(foreign, OtherSession, () => signedOut.Add("foreign"));

        _tracker.Connected.ShouldBe([Session, OtherSession], ignoreOrder: true);
        _tracker.SetCircuitConnected(foreign, connected: false);
        _tracker.Connected.ShouldBe([Session]);

        _tracker.SignalEnded(Session);
        signedOut.ShouldBe(["first"]); // сломанное соединение не помешало первому

        _tracker.UnregisterCircuit(first);
        _tracker.UnregisterCircuit(second);
        _tracker.Connected.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Вход заводит сеанс с IP и браузером; выход закрывает его как «вышел» и гасит кэш")]
    public async Task Start_and_end()
    {
        _store.StartAsync(Arg.Any<UserSessionStart>(), Arg.Any<CancellationToken>()).Returns(OtherSession);
        _store.EndAsync(OtherSession, UserSessionEndReason.SignedOut, null, null, Arg.Any<CancellationToken>()).Returns(true);

        var id = await _tracker.StartAsync(7, "10.0.0.5", "Edg/141", CancellationToken.None);
        id.ShouldBe(OtherSession);
        await _store.Received(1).StartAsync(new UserSessionStart(7, "10.0.0.5", "Edg/141"), Arg.Any<CancellationToken>());
        (await _tracker.IsActiveAsync(OtherSession, CancellationToken.None)).ShouldBeTrue();
        await _store.DidNotReceive().IsActiveAsync(OtherSession, Arg.Any<CancellationToken>()); // только что заведён — из кэша

        (await _tracker.EndAsync(OtherSession, UserSessionEndReason.SignedOut, CancellationToken.None)).ShouldBeTrue();
        (await _tracker.IsActiveAsync(OtherSession, CancellationToken.None)).ShouldBeFalse();
    }

    [Fact(DisplayName = "Номер сеанса из cookie: есть и разбирается — номер; нет или мусор — нет")]
    public void Session_id_from_claims()
    {
        UserSessionTracker.SessionIdOf(Principal(Session.ToString("D"))).ShouldBe(Session);
        UserSessionTracker.SessionIdOf(Principal(null)).ShouldBeNull();
        UserSessionTracker.SessionIdOf(Principal("не-номер")).ShouldBeNull();
        AuthClaims.SessionId.ShouldBe(UserSessionClaims.SessionId);
    }

    [Fact(DisplayName = "Проверка cookie: без номера сеанса — отклонена и снята; завершённый сеанс — отклонена; действующий — принята, активность отмечена")]
    public async Task Cookie_validation_checks_session()
    {
        var noSession = await ValidateAsync(Principal(null));
        noSession.Context.Principal.ShouldBeNull();
        await noSession.Auth.Received(1).SignOutAsync(Arg.Any<HttpContext>(), CookieAuthenticationDefaults.AuthenticationScheme, Arg.Any<AuthenticationProperties?>());

        _store.IsActiveAsync(OtherSession, Arg.Any<CancellationToken>()).Returns(false);
        var ended = await ValidateAsync(Principal(OtherSession.ToString("D")));
        ended.Context.Principal.ShouldBeNull();

        var active = await ValidateAsync(Principal(Session.ToString("D")));
        active.Context.Principal.ShouldNotBeNull();
        await active.Auth.DidNotReceiveWithAnyArgs().SignOutAsync(default!, default, default);
        await _store.Received(1).TouchAsync(Session, Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    private async Task<(CookieValidatePrincipalContext Context, IAuthenticationService Auth)> ValidateAsync(ClaimsPrincipal principal)
    {
        var auth = Substitute.For<IAuthenticationService>();
        var services = new ServiceCollection()
            .AddSingleton(_tracker)
            .AddSingleton(auth)
            .AddSingleton<IConfiguration>(new ConfigurationBuilder().Build())
            .BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };
        var context = new CookieValidatePrincipalContext(
            http,
            new AuthenticationScheme(CookieAuthenticationDefaults.AuthenticationScheme, null, typeof(CookieAuthenticationHandler)),
            new CookieAuthenticationOptions(),
            new AuthenticationTicket(principal, CookieAuthenticationDefaults.AuthenticationScheme));

        await CookiePrincipalValidator.ValidateAsync(context);
        return (context, auth);
    }

    // Принципал с отметкой «проверен только что» — окно ревалидации внешней системы не истекло, проверяется только сеанс.
    private static ClaimsPrincipal Principal(string? sessionId)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "7"),
            new(AuthClaims.ExternalId, "ext-7"),
            new(AuthClaims.SecurityStamp, "stamp"),
            new(AuthClaims.ValidatedAt, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)),
        };
        if (sessionId is not null)
        {
            claims.Add(new Claim(AuthClaims.SessionId, sessionId));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
