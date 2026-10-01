using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.BackgroundTasks;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Application;
using ISC.AI.Modules.Media.Application.Features.Suggestions;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Media;

/// <summary>
/// Карточка носителя и «Сверить сейчас» (ТФ-ПЕР-09, ADR-0035) без БД: состояние сверки по каждому доступному делу
/// (почему не сверялось — закрыто, нет основания, нет эталонов; сверено или устарело), запуск сверки только по праву
/// и только обработанного носителя с лицами, постановка в фоновую очередь без падения вызвавшей операции.
/// </summary>
public sealed class PersonSuggestionStatusTests
{
    private const int AssetId = 31;

    private static readonly DateTime RunAt = new(2026, 10, 1, 8, 28, 0, DateTimeKind.Utc);
    private static readonly AccessContext Access = new("1", 3, [1]);
    private static readonly int[] BothCases = [7, 9];

    private readonly IAccessContextProvider _accessProvider = Substitute.For<IAccessContextProvider>();
    private readonly ICaseScope _caseScope = Substitute.For<ICaseScope>();
    private readonly ISuggestionRunStore _runs = Substitute.For<ISuggestionRunStore>();
    private readonly ISearchSessionStore _sessions = Substitute.For<ISearchSessionStore>();
    private readonly IMediaAdministration _administration = Substitute.For<IMediaAdministration>();
    private readonly IMediaCatalog _catalog = Substitute.For<IMediaCatalog>();
    private readonly IPersonSuggestionScheduler _scheduler = Substitute.For<IPersonSuggestionScheduler>();

    public PersonSuggestionStatusTests()
    {
        _accessProvider.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(Access);
        _caseScope.IsAssetAccessibleAsync(AssetId, Access, Arg.Any<CancellationToken>()).Returns(true);
        _administration.CanUploadAsync(Arg.Any<CancellationToken>()).Returns(true);
        _catalog.GetAsync(AssetId, Access, Arg.Any<CancellationToken>()).Returns(Asset(MediaKind.Image, MediaIndexStatus.Indexed, 1));
        _scheduler.ScheduleAssetAsync(AssetId, SuggestionTrigger.Manual, Arg.Any<CancellationToken>()).Returns(Guid.NewGuid());
    }

    [Theory(DisplayName = "Состояние дела: закрыто → нет основания → нет эталонов → не сверялось → устарело → сверено")]
    [InlineData(false, true, 1, false, null, CaseSuggestionState.Closed)]
    [InlineData(true, false, 1, true, null, CaseSuggestionState.NoBasis)]
    [InlineData(true, true, 0, true, null, CaseSuggestionState.NoReferences)]
    [InlineData(true, true, 2, false, null, CaseSuggestionState.NotChecked)]
    [InlineData(true, true, 2, true, 10, CaseSuggestionState.Outdated)]
    [InlineData(true, true, 2, true, -10, CaseSuggestionState.Checked)]
    public void State_follows_readiness_and_run_age(
        bool open, bool basis, int references, bool hasRun, int? referenceMinutesAfterRun, CaseSuggestionState expected)
    {
        var readiness = new SuggestionReadiness(
            7, "В-3/26", open, basis, references, referenceMinutesAfterRun is { } m ? RunAt.AddMinutes(m) : null);
        var run = hasRun ? Run(7) : null;

        GetAssetSuggestionStatusQuery.Handler.StateOf(readiness, run).ShouldBe(expected);
    }

    [Fact(DisplayName = "Карточка: по каждому доступному делу — состояние и последняя сверка; предложения — только по этим делам; кнопка — по праву")]
    public async Task Status_combines_readiness_runs_and_suggestions()
    {
        _caseScope.ListSuggestionReadinessAsync(AssetId, Access, Arg.Any<CancellationToken>()).Returns(
        [
            new SuggestionReadiness(7, "В-3/26", true, true, 1, RunAt.AddMinutes(-5)),
            new SuggestionReadiness(9, "Т-1", true, false, 0, null),
        ]);
        _runs.ListLatestAsync(AssetId, Arg.Any<IReadOnlyCollection<int>>(), Access, Arg.Any<CancellationToken>()).Returns([Run(7)]);
        var suggestion = new SuggestedCandidateRow(945, 92, 7, 74, 0.65, CandidateStatus.Candidate, 11);
        _sessions.ListSuggestedForAssetAsync(AssetId, Arg.Any<IReadOnlyCollection<int>>(), Access, Arg.Any<CancellationToken>())
            .Returns([suggestion]);

        var response = await StatusHandler().Handle(new GetAssetSuggestionStatusQuery(AssetId), CancellationToken.None);

        var status = response.Data.ShouldNotBeNull();
        status.Enabled.ShouldBeTrue();
        status.CanRequest.ShouldBeTrue();
        status.MaxCosineDistance.ShouldBe(MediaSearchOptions.DefaultAutoSuggestMaxCosineDistance);
        status.Cases.Select(c => (c.CaseId, c.State)).ShouldBe([(7, CaseSuggestionState.Checked), (9, CaseSuggestionState.NoBasis)]);
        status.Cases[0].LastRun.ShouldNotBeNull().CandidatesCreated.ShouldBe(1);
        status.Suggestions.ShouldHaveSingleItem().ShouldBe(suggestion);
        await _sessions.Received(1).ListSuggestedForAssetAsync(
            AssetId, Arg.Is<IReadOnlyCollection<int>>(ids => ids.SequenceEqual(BothCases)), Access, Arg.Any<CancellationToken>());

        // Без права на загрузку (Следователь/Администратор) кнопки нет; без готового дела — тоже.
        _administration.CanUploadAsync(Arg.Any<CancellationToken>()).Returns(false);
        (await StatusHandler().Handle(new GetAssetSuggestionStatusQuery(AssetId), CancellationToken.None)).Data!.CanRequest.ShouldBeFalse();
    }

    [Fact(DisplayName = "Карточка чужого носителя — единый «не найден», порты сверки не спрашиваются (ТБ-020/021)")]
    public async Task Status_of_inaccessible_asset_is_not_found()
    {
        _caseScope.IsAssetAccessibleAsync(AssetId, Access, Arg.Any<CancellationToken>()).Returns(false);

        var response = await StatusHandler().Handle(new GetAssetSuggestionStatusQuery(AssetId), CancellationToken.None);

        response.StatusCode.ShouldBe(ResponseStatusCode.NotFound);
        await _caseScope.DidNotReceiveWithAnyArgs().ListSuggestionReadinessAsync(default, default!, default);
        await _runs.DidNotReceiveWithAnyArgs().ListLatestAsync(default, default!, default!, default);
    }

    [Fact(DisplayName = "«Сверить сейчас»: обработанный носитель с лицами — сверка поставлена с поводом «по кнопке»")]
    public async Task Request_schedules_manual_suggestion()
    {
        var response = await RequestHandler().Handle(new RequestPersonSuggestionCommand(AssetId), CancellationToken.None);

        response.Status.ShouldBeTrue();
        await _scheduler.Received(1).ScheduleAssetAsync(AssetId, SuggestionTrigger.Manual, Arg.Any<CancellationToken>());
        new RequestPersonSuggestionCommand(AssetId).AuditSummary.ShouldBe("media:suggest:request:asset=31");
    }

    [Fact(DisplayName = "«Сверить сейчас» отказывает: выключено, нет права, чужой носитель, аудио, не обработан, нет лиц, очередь недоступна")]
    public async Task Request_refusals()
    {
        (await RequestHandler(new MediaSearchOptions(AutoSuggestEnabled: false)).Handle(new RequestPersonSuggestionCommand(AssetId), default))
            .Status.ShouldBeFalse();

        _administration.CanUploadAsync(Arg.Any<CancellationToken>()).Returns(false);
        (await RequestHandler().Handle(new RequestPersonSuggestionCommand(AssetId), default)).StatusMessage.ShouldNotBeNull().ShouldContain("Следователь");
        _administration.CanUploadAsync(Arg.Any<CancellationToken>()).Returns(true);

        _caseScope.IsAssetAccessibleAsync(AssetId, Access, Arg.Any<CancellationToken>()).Returns(false);
        (await RequestHandler().Handle(new RequestPersonSuggestionCommand(AssetId), default)).StatusCode.ShouldBe(ResponseStatusCode.NotFound);
        _caseScope.IsAssetAccessibleAsync(AssetId, Access, Arg.Any<CancellationToken>()).Returns(true);

        foreach (var asset in new[]
        {
            Asset(MediaKind.Audio, MediaIndexStatus.Indexed, 0),
            Asset(MediaKind.Image, MediaIndexStatus.Processing, 0),
            Asset(MediaKind.Image, MediaIndexStatus.Indexed, 0),
        })
        {
            _catalog.GetAsync(AssetId, Access, Arg.Any<CancellationToken>()).Returns(asset);
            (await RequestHandler().Handle(new RequestPersonSuggestionCommand(AssetId), default)).Status.ShouldBeFalse();
        }

        _catalog.GetAsync(AssetId, Access, Arg.Any<CancellationToken>()).Returns(Asset(MediaKind.Image, MediaIndexStatus.Indexed, 1));
        _scheduler.ScheduleAssetAsync(AssetId, SuggestionTrigger.Manual, Arg.Any<CancellationToken>()).Returns((Guid?)null);
        (await RequestHandler().Handle(new RequestPersonSuggestionCommand(AssetId), default)).StatusMessage.ShouldNotBeNull().ShouldContain("очередь");

        await _scheduler.Received(1).ScheduleAssetAsync(AssetId, SuggestionTrigger.Manual, Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Планировщик: задача вызывает сверку дела; выключено — не ставит; сбой очереди — null без исключения")]
    public async Task Scheduler_enqueues_and_swallows_failures()
    {
        var queue = Substitute.For<IBackgroundTaskQueue>();
        Func<IServiceProvider, CancellationToken, Task>? work = null;
        var taskId = Guid.NewGuid();
        // Настройка подмены NSubstitute: ValueTask здесь не исполняется, а задаёт ответ (CA2012 к этому неприменим).
#pragma warning disable CA2012
        queue.EnqueueAsync(PersonSuggestionScheduler.CaseSweepKind, Arg.Do<Func<IServiceProvider, CancellationToken, Task>>(w => work = w), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<Guid>(taskId));
#pragma warning restore CA2012

        (await Scheduler(queue).ScheduleCaseSweepAsync(7)).ShouldBe(taskId);

        var suggester = Substitute.For<IPersonSuggester>();
        await using (var provider = new ServiceCollection().AddSingleton(suggester).BuildServiceProvider())
        {
            await work.ShouldNotBeNull()(provider, CancellationToken.None);
        }

        await suggester.Received(1).SuggestForCaseAsync(7, Arg.Any<CancellationToken>());

        (await Scheduler(queue, new MediaSearchOptions(AutoSuggestEnabled: false)).ScheduleAssetAsync(AssetId, SuggestionTrigger.Manual)).ShouldBeNull();
        await queue.DidNotReceive().EnqueueAsync(
            PersonSuggestionScheduler.AssetKind, Arg.Any<Func<IServiceProvider, CancellationToken, Task>>(), Arg.Any<CancellationToken>());
        queue.ClearReceivedCalls();
#pragma warning disable CA2012
        queue.EnqueueAsync(Arg.Any<string>(), Arg.Any<Func<IServiceProvider, CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("БД задач недоступна"));
#pragma warning restore CA2012
        (await Scheduler(queue).ScheduleAssetAsync(AssetId, SuggestionTrigger.Manual)).ShouldBeNull();
    }

    private GetAssetSuggestionStatusQuery.Handler StatusHandler(MediaSearchOptions? options = null) =>
        new(_accessProvider, _caseScope, _runs, _sessions, _administration, options ?? new MediaSearchOptions());

    private RequestPersonSuggestionCommand.Handler RequestHandler(MediaSearchOptions? options = null) =>
        new(_administration, _accessProvider, _catalog, _caseScope, _scheduler, options ?? new MediaSearchOptions());

    private static PersonSuggestionScheduler Scheduler(IBackgroundTaskQueue queue, MediaSearchOptions? options = null) =>
        new(queue, options ?? new MediaSearchOptions(), NullLogger<PersonSuggestionScheduler>.Instance);

    private static SuggestionRunRow Run(int caseId) =>
        new(1, AssetId, caseId, SuggestionTrigger.Indexing, 1, 1, 1, 0.5, RunAt);

    private static MediaAssetRow Asset(MediaKind kind, MediaIndexStatus status, int faces) => new(
        AssetId, kind, "photo.jpg", "s.jpg", "image/jpeg", 10, null, null, null, 3, 1, 1, status, null,
        "yunet-1", "sface-1", null, RunAt, faces);
}
