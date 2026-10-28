using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Backup;
using ISC.AI.Abstractions.Profiles;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Admin.Application;
using ISC.AI.Modules.Admin.Application.Features.Backups;
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
/// «Резервные копии» (ТФ-АДМ-04, ТБ-082, ADR-0063): право профиля, имена запускавших, напоминание через 30 дней, запуск и
/// проверка с отказами ядра текстом, журнал запуска; страница — что не настроено, носитель, ход копии и итоги.
/// </summary>
public sealed class BackupsTests : BunitContext, IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);

    private readonly IBackupService _backups = Substitute.For<IBackupService>();
    private readonly IPlatformAdministration _administration = Substitute.For<IPlatformAdministration>();
    private readonly IUserAccountStore _accounts = Substitute.For<IUserAccountStore>();
    private readonly ISubjectProvider _subject = Substitute.For<ISubjectProvider>();
    private readonly IMediator _mediator = Substitute.For<IMediator>();

    public BackupsTests()
    {
        _administration.CanManageBackupsAsync(Arg.Any<CancellationToken>()).Returns(true);
        _subject.GetCurrentUserIdAsync(Arg.Any<CancellationToken>()).Returns(7);
        _accounts.ListAsync(Arg.Any<CancellationToken>()).Returns(
            [new UserAccountRow(7, "ashyrov", "Ашыров Б. Д.", IsActive: true, HasLocalPassword: true, MustChangePassword: false)]);
        _backups.GetOverviewAsync(Arg.Any<CancellationToken>()).Returns(Overview([Run(1, BackupRunStatus.Succeeded, daysAgo: 3)]));

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

    [Fact(DisplayName = "Состояние: без права — отказ, ядро не спрашивается; с правом — имена запускавших, последняя успешная, без напоминания")]
    public async Task Overview_requires_right_and_names_users()
    {
        _administration.CanManageBackupsAsync(Arg.Any<CancellationToken>()).Returns(false);
        (await GetAsync()).StatusMessage.ShouldBe(AdminGuard.BackupsDenied);
        await _backups.DidNotReceiveWithAnyArgs().GetOverviewAsync(default);

        _administration.CanManageBackupsAsync(Arg.Any<CancellationToken>()).Returns(true);
        var view = (await GetAsync()).Data!;
        view.UserNames[7].ShouldBe("Ашыров Б. Д.");
        view.LastSuccessAtUtc.ShouldBe(Now.UtcDateTime.AddDays(-3).AddHours(1));
        view.ReminderDue.ShouldBeFalse();
        view.Busy.ShouldBeFalse();
    }

    [Fact(DisplayName = "Напоминание: успешных копий нет или последней больше 30 дней — пора; неудачные не в счёт")]
    public async Task Reminder_after_thirty_days()
    {
        _backups.GetOverviewAsync(Arg.Any<CancellationToken>()).Returns(Overview([Run(2, BackupRunStatus.Failed, daysAgo: 1)]));
        (await GetAsync()).Data!.ReminderDue.ShouldBeTrue();

        _backups.GetOverviewAsync(Arg.Any<CancellationToken>()).Returns(Overview([Run(3, BackupRunStatus.Succeeded, daysAgo: 31)]));
        (await GetAsync()).Data!.ReminderDue.ShouldBeTrue();
    }

    [Fact(DisplayName = "Запуск и проверка: без права — отказ; отказ ядра — текстом; журнал — Export запуска и View проверки")]
    public async Task Start_and_verify()
    {
        new StartBackupCommand().AuditAction.ShouldBe(AuditAction.Export);
        new StartBackupCommand().AuditSummary.ShouldBe("admin:backup:start");
        new VerifyBackupCommand().AuditAction.ShouldBe(AuditAction.View);

        _backups.StartAsync(7, Arg.Any<CancellationToken>()).Returns(new BackupActionResult(5, null));
        var started = await new StartBackupCommand.Handler(_backups, _administration, _subject).Handle(new StartBackupCommand(), CancellationToken.None);
        (started.Status, started.Data).ShouldBe((true, 5));

        _backups.VerifyLatestAsync(7, Arg.Any<CancellationToken>()).Returns(new BackupActionResult(null, "На подключённом носителе нет готовой копии из журнала."));
        var verify = await new VerifyBackupCommand.Handler(_backups, _administration, _subject).Handle(new VerifyBackupCommand(), CancellationToken.None);
        verify.StatusMessage.ShouldBe("На подключённом носителе нет готовой копии из журнала.");

        _administration.CanManageBackupsAsync(Arg.Any<CancellationToken>()).Returns(false);
        (await new StartBackupCommand.Handler(_backups, _administration, _subject).Handle(new StartBackupCommand(), CancellationToken.None))
            .StatusMessage.ShouldBe(AdminGuard.BackupsDenied);
        await _backups.Received(1).StartAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Страница: что не настроено — списком, «Сделать копию» недоступна; носитель не подключён — подсказка")]
    public void Page_shows_problems()
    {
        GivenView(Overview([], problems: ["Не задан открытый ключ Заказчика (Backup:PublicKey:Path)."], available: false), due: true);
        var cut = Render<Backups>();

        cut.Markup.ShouldContain("Не задан открытый ключ Заказчика");
        cut.Markup.ShouldContain("Успешных копий ещё не было");
        cut.Markup.ShouldContain("Подключите носитель");
        Button(cut, "Сделать копию").HasAttribute("disabled").ShouldBeTrue();
        cut.Markup.ShouldContain("Копий ещё не делали");
    }

    [Fact(DisplayName = "Страница: идёт копия — ход в байтах, кнопки недоступны; готовая и проверенная — итог и «цела»")]
    public void Page_shows_progress_and_results()
    {
        var running = Run(9, BackupRunStatus.Running, daysAgo: 0) with { BytesWritten = 5L * 1024 * 1024 * 1024, FinishedAtUtc = null };
        var verified = Run(8, BackupRunStatus.Succeeded, daysAgo: 2) with { VerifyStatus = BackupVerifyStatus.Ok, VerifiedAtUtc = Now.UtcDateTime };
        GivenView(Overview([running, verified]), due: false);
        var cut = Render<Backups>();

        cut.Markup.ShouldContain("Копия пишется: записано 5,0 ГБ");
        Button(cut, "Сделать копию").HasAttribute("disabled").ShouldBeTrue();
        Button(cut, "Проверить последнюю копию").HasAttribute("disabled").ShouldBeTrue();
        cut.Markup.ShouldContain("готова");
        cut.Markup.ShouldContain("цела");
        cut.Markup.ShouldContain("Ашыров Б. Д.");
    }

    [Fact(DisplayName = "Страница: «Сделать копию» — команда и сообщение; копию можно делать только при готовности")]
    public void Page_starts_backup()
    {
        GivenView(Overview([Run(1, BackupRunStatus.Succeeded, daysAgo: 3)]), due: false);

        // Настройка подмены NSubstitute: ValueTask здесь не исполняется, а задаёт ответ и проверяет вызов (CA2012 к этому неприменим).
#pragma warning disable CA2012
        _mediator.Send(Arg.Any<StartBackupCommand>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<int>>(ResponseDto<int>.Ok(2, "Копия запущена. Не отключайте носитель до окончания.")));
        var snackbars = Render<MudSnackbarProvider>();
        var cut = Render<Backups>();

        Button(cut, "Сделать копию").Click();

        cut.WaitForAssertion(() => _mediator.Received(1).Send(Arg.Any<StartBackupCommand>(), Arg.Any<CancellationToken>()));
#pragma warning restore CA2012
        cut.WaitForAssertion(() => snackbars.Markup.ShouldContain("Копия запущена"));
    }

    private async Task<ResponseDto<BackupOverviewView>> GetAsync() =>
        await new GetBackupOverviewQuery.Handler(_backups, _administration, _accounts, new FixedClock(Now))
            .Handle(new GetBackupOverviewQuery(), CancellationToken.None);

    // Настройка подмены NSubstitute: ValueTask здесь не исполняется, а задаёт ответ (CA2012 к этому неприменим).
#pragma warning disable CA2012
    private void GivenView(BackupOverview overview, bool due) =>
        _mediator.Send(Arg.Any<GetBackupOverviewQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<BackupOverviewView>>(ResponseDto<BackupOverviewView>.Ok(new BackupOverviewView(
                overview, new Dictionary<int, string> { [7] = "Ашыров Б. Д." },
                overview.Runs.Where(r => r.Status == BackupRunStatus.Succeeded).Select(r => r.FinishedAtUtc).Max(), due))));
#pragma warning restore CA2012

    private static BackupOverview Overview(IReadOnlyList<BackupRunRow> runs, IReadOnlyList<string>? problems = null, bool available = true) =>
        new(problems ?? [], @"E:\ISC-Backups", available, available ? 900L * 1024 * 1024 * 1024 : null, 12L * 1024 * 1024 * 1024,
            "AA3CFC16963A10586A9393F5035D6D6B57E98D358B347F80C2A30BF4F00CEBA2", 30, runs);

    private static BackupRunRow Run(int id, BackupRunStatus status, int daysAgo) =>
        new(id, Now.UtcDateTime.AddDays(-daysAgo), Now.UtcDateTime.AddDays(-daysAgo).AddHours(1), status, 7, $"isc-backup-{id}", 1, 120, 900_000,
            1_200_000, status == BackupRunStatus.Failed ? "pg_dump завершился с кодом 1" : null, null, null, null);

    private static AngleSharp.Dom.IElement Button<T>(IRenderedComponent<T> cut, string text)
        where T : IComponent =>
        cut.FindAll("button").Single(b => b.TextContent.Contains(text, StringComparison.Ordinal));

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
