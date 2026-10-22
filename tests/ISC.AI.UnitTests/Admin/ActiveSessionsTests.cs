using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Profiles;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Admin.Application;
using ISC.AI.Modules.Admin.Application.Features.Audit;
using ISC.AI.Modules.Admin.Application.Features.Sessions;
using ISC.AI.Modules.Admin.Domain.Services;
using ISC.AI.Modules.Admin.UI;
using Mediator;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Admin;

/// <summary>
/// Активные сеансы (ТФ-АДМ-09, ADR-0060): список под правом профиля, окно активности по таймауту неактивности, «на
/// связи» и свой сеанс; завершение — с причиной, не своего сеанса, только действующего, с мгновенным сигналом
/// соединениям; запись в журнал; подпись рабочего места; страница.
/// </summary>
public sealed class ActiveSessionsTests : BunitContext, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid Own = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Other = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Idle = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private const string EdgeWindows =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36 Edg/141.0.0.0";

    private readonly IUserSessionStore _store = Substitute.For<IUserSessionStore>();
    private readonly IUserSessionMonitor _monitor = Substitute.For<IUserSessionMonitor>();
    private readonly IPlatformAdministration _administration = Substitute.For<IPlatformAdministration>();
    private readonly ISubjectProvider _subject = Substitute.For<ISubjectProvider>();
    private readonly IMediator _mediator = Substitute.For<IMediator>();

    public ActiveSessionsTests()
    {
        _administration.CanManageSessionsAsync(Arg.Any<CancellationToken>()).Returns(true);
        _subject.GetCurrentUserIdAsync(Arg.Any<CancellationToken>()).Returns(7);
        _subject.GetCurrentSessionIdAsync(Arg.Any<CancellationToken>()).Returns(Own);
        _monitor.IdleTimeout.Returns(TimeSpan.FromMinutes(30));
        _monitor.Connected.Returns([Own, Other]);
        _store.ListOpenAsync(Arg.Any<DateTime>(), Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(
            [
                new UserSessionRow(Own, 7, "ashyrov", "Ашыров Б. Д.", "10.0.0.5", EdgeWindows, Now.UtcDateTime.AddHours(-2), Now.UtcDateTime),
                new UserSessionRow(Other, 8, "sadykov", null, "10.0.0.9", null, Now.UtcDateTime.AddHours(-1), Now.UtcDateTime.AddMinutes(-5)),
                new UserSessionRow(Idle, 9, "omurov", "Омуров К.", null, "curl/8.0", Now.UtcDateTime.AddHours(-3), Now.UtcDateTime.AddMinutes(-20)),
            ]);
        _store.EndAsync(Arg.Any<Guid>(), Arg.Any<UserSessionEndReason>(), Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton(_mediator);
        Services.AddSingleton<TimeProvider>(new FixedClock(Now));
        var profile = Substitute.For<IProfile>();
        profile.DisplayName.Returns("Следствие");
        Services.AddSingleton(profile);
        SetRendererInfo(new RendererInfo("Server", true));
    }

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

    Task IAsyncLifetime.DisposeAsync() => base.DisposeAsync().AsTask();

    [Fact(DisplayName = "Список: окно активности — сейчас минус таймаут, «на связи» по соединениям, свой сеанс помечен, рабочее место словами")]
    public async Task List_marks_connected_and_current()
    {
        var view = (await ListHandler().Handle(new ListActiveSessionsQuery(" ivan "), CancellationToken.None)).Data.ShouldNotBeNull();

        await _store.Received(1).ListOpenAsync(
            Now.UtcDateTime.AddMinutes(-30),
            Arg.Is<IReadOnlyCollection<Guid>>(c => c.Contains(Own) && c.Contains(Other)),
            " ivan ",
            Arg.Any<CancellationToken>());
        view.IdleMinutes.ShouldBe(30);
        view.Rows.Select(r => (r.SessionId, r.Connected, r.IsCurrent)).ShouldBe([(Own, true, true), (Other, true, false), (Idle, false, false)]);
        view.Rows[0].Workstation.ShouldBe("Edge · Windows");
        view.Rows[1].Workstation.ShouldBe(WorkstationLabel.Unknown);
        new ListActiveSessionsQuery().AuditSummary.ShouldBe("admin:user-sessions:view");
    }

    [Fact(DisplayName = "Без права — отказ текстом, хранилище не спрашивается; без права и завершить нельзя")]
    public async Task Without_right_nothing_is_read_or_ended()
    {
        _administration.CanManageSessionsAsync(Arg.Any<CancellationToken>()).Returns(false);

        (await ListHandler().Handle(new ListActiveSessionsQuery(), CancellationToken.None)).StatusMessage.ShouldBe(AdminGuard.SessionsDenied);
        (await TerminateHandler().Handle(new TerminateUserSessionCommand(Other, "уволен сегодня"), CancellationToken.None))
            .StatusMessage.ShouldBe(AdminGuard.SessionsDenied);

        await _store.DidNotReceiveWithAnyArgs().ListOpenAsync(default, default!, default, default);
        await _store.DidNotReceiveWithAnyArgs().EndAsync(default, default, default, default, default);
        _monitor.DidNotReceiveWithAnyArgs().SignalEnded(default);
    }

    [Fact(DisplayName = "Завершение: «завершён» с автором и причиной без пробелов по краям, соединения сеанса — сигнал сразу; журнал — номер сеанса")]
    public async Task Terminate_ends_and_signals()
    {
        var response = await TerminateHandler().Handle(new TerminateUserSessionCommand(Other, "  чужой компьютер  "), CancellationToken.None);

        response.Status.ShouldBeTrue();
        await _store.Received(1).EndAsync(Other, UserSessionEndReason.Terminated, 7, "чужой компьютер", Arg.Any<CancellationToken>());
        _monitor.Received(1).SignalEnded(Other);
        new TerminateUserSessionCommand(Other, "x").AuditSummary.ShouldBe("admin:user-session:22222222-2222-2222-2222-222222222222:terminate");
        AuditText.Describe(Abstractions.Audit.AuditAction.Modify, "admin:user-session:x:terminate", null)
            .ShouldBe("Администрирование · сеанс входа · x · завершение");
    }

    [Fact(DisplayName = "Свой текущий сеанс не завершается с экрана; уже закрытый — «уже завершён», без сигнала; без субъекта — отказ")]
    public async Task Terminate_refusals()
    {
        (await TerminateHandler().Handle(new TerminateUserSessionCommand(Own, "проверка"), CancellationToken.None))
            .StatusMessage.ShouldBe(TerminateUserSessionCommand.OwnSession);
        await _store.DidNotReceive().EndAsync(Own, Arg.Any<UserSessionEndReason>(), Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());

        _store.EndAsync(Idle, Arg.Any<UserSessionEndReason>(), Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(false);
        var ended = await TerminateHandler().Handle(new TerminateUserSessionCommand(Idle, "проверка"), CancellationToken.None);
        ended.StatusCode.ShouldBe(ResponseStatusCode.NotFound);
        ended.StatusMessage.ShouldBe(TerminateUserSessionCommand.AlreadyEnded);
        _monitor.DidNotReceive().SignalEnded(Idle);

        _subject.GetCurrentUserIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        (await TerminateHandler().Handle(new TerminateUserSessionCommand(Other, "проверка"), CancellationToken.None))
            .StatusMessage.ShouldBe(AdminGuard.SessionsDenied);
    }

    [Theory(DisplayName = "Причина обязательна: от 5 до 500 символов без пробелов по краям; номер сеанса — не пустой")]
    [InlineData("уволен", true)]
    [InlineData("  ок  ", false)]
    [InlineData("     ", false)]
    [InlineData(null, false)]
    public void Reason_is_validated(string? reason, bool valid)
    {
        new TerminateUserSessionValidator().Validate(new TerminateUserSessionCommand(Other, reason!)).IsValid.ShouldBe(valid);
    }

    [Fact(DisplayName = "Причина длиннее 500 символов и пустой номер сеанса — отказ проверки")]
    public void Reason_too_long_and_empty_session_are_rejected()
    {
        var validator = new TerminateUserSessionValidator();
        validator.Validate(new TerminateUserSessionCommand(Other, new string('а', 501))).IsValid.ShouldBeFalse();
        validator.Validate(new TerminateUserSessionCommand(Guid.Empty, "уволен")).IsValid.ShouldBeFalse();
    }

    [Theory(DisplayName = "Рабочее место по строке браузера: Edge, Яндекс, Firefox, Chrome на Android; нет строки — «не определено»")]
    [InlineData(EdgeWindows, "Edge · Windows")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 YaBrowser/25.8.0.0 Safari/537.36", "Яндекс Браузер · Windows")]
    [InlineData("Mozilla/5.0 (X11; Linux x86_64; rv:140.0) Gecko/20100101 Firefox/140.0", "Firefox · Linux")]
    [InlineData("Mozilla/5.0 (Linux; Android 14; K) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Mobile Safari/537.36", "Chrome · Android")]
    [InlineData("curl/8.0", "другой браузер")]
    [InlineData(null, WorkstationLabel.Unknown)]
    public void Workstation_is_described(string? userAgent, string expected)
    {
        WorkstationLabel.Describe(userAgent).ShouldBe(expected);
    }

    [Fact(DisplayName = "Страница: строки с ФИО, IP и рабочим местом, «на связи»; у своего сеанса нет «Завершить…», у чужого — есть")]
    public void Page_lists_sessions()
    {
        SetupListResponse();
        var cut = Render<ActiveSessions>();

        cut.Markup.ShouldContain("Ашыров Б. Д.");
        cut.Markup.ShouldContain("ashyrov · ваш текущий сеанс");
        cut.Markup.ShouldContain("10.0.0.9");
        cut.Markup.ShouldContain("Edge · Windows");
        cut.Markup.ShouldContain("на связи");
        cut.Markup.ShouldContain("без связи");
        cut.Markup.ShouldContain("5 мин назад");
        cut.Markup.ShouldContain("Сеансов: 3 · на связи: 2");
        cut.FindAll("button").Count(b => b.TextContent.Contains("Завершить…", StringComparison.Ordinal)).ShouldBe(2);
    }

    [Fact(DisplayName = "Страница: «Завершить» неактивна без причины, с причиной шлёт команду и перечитывает список")]
    public void Page_terminates_with_reason()
    {
        SetupListResponse();
        _mediator.Send(Arg.Any<TerminateUserSessionCommand>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<bool>>(ResponseDto<bool>.Ok(true)));
        Render<MudPopoverProvider>();
        var dialogs = Render<MudDialogProvider>();
        var cut = Render<ActiveSessions>();

        cut.FindAll("button").First(b => b.TextContent.Contains("Завершить…", StringComparison.Ordinal)).Click();
        dialogs.WaitForAssertion(() => dialogs.Markup.ShouldContain("Завершить сеанс?"));
        dialogs.Markup.ShouldContain("sadykov");
        Confirm(dialogs).HasAttribute("disabled").ShouldBeTrue();

        dialogs.Find("textarea").Input("  чужой компьютер ");
        Confirm(dialogs).HasAttribute("disabled").ShouldBeFalse();
        Confirm(dialogs).Click();

        _mediator.Received(1).Send(
            Arg.Is<TerminateUserSessionCommand>(c => c.SessionId == Other && c.Reason == "чужой компьютер"), Arg.Any<CancellationToken>());
        _mediator.Received(2).Send(Arg.Any<ListActiveSessionsQuery>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Страница: отказ сервера показан текстом")]
    public void Page_shows_denial()
    {
        _mediator.Send(Arg.Any<ListActiveSessionsQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<ActiveSessionsView>>(ResponseDto<ActiveSessionsView>.BadRequest(AdminGuard.SessionsDenied)));

        var cut = Render<ActiveSessions>();

        cut.Markup.ShouldContain(AdminGuard.SessionsDenied);
        cut.Markup.ShouldContain("Активных сеансов по этому отбору нет.");
    }

    private static AngleSharp.Dom.IElement Confirm(IRenderedComponent<MudDialogProvider> dialogs) =>
        dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "Завершить");

    private void SetupListResponse()
    {
        var view = new ActiveSessionsView(
        [
            new ActiveSessionRow(Own, 7, "ashyrov", "Ашыров Б. Д.", "10.0.0.5", EdgeWindows, "Edge · Windows",
                Now.UtcDateTime.AddHours(-2), Now.UtcDateTime, Connected: true, IsCurrent: true),
            new ActiveSessionRow(Other, 8, "sadykov", null, "10.0.0.9", null, WorkstationLabel.Unknown,
                Now.UtcDateTime.AddHours(-1), Now.UtcDateTime.AddMinutes(-5), Connected: true, IsCurrent: false),
            new ActiveSessionRow(Idle, 9, "omurov", "Омуров К.", null, "curl/8.0", "другой браузер",
                Now.UtcDateTime.AddHours(-3), Now.UtcDateTime.AddMinutes(-20), Connected: false, IsCurrent: false),
        ], 30);
        _mediator.Send(Arg.Any<ListActiveSessionsQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<ActiveSessionsView>>(ResponseDto<ActiveSessionsView>.Ok(view)));
    }

    private ListActiveSessionsQuery.Handler ListHandler() => new(_store, _monitor, _administration, _subject, new FixedClock(Now));

    private TerminateUserSessionCommand.Handler TerminateHandler() => new(_store, _monitor, _subject, _administration);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
