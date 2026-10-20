using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Profiles;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Profile.Investigation.Application.Features.Search;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using ISC.AI.Profile.Investigation.UI;
using Mediator;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Investigation.UI;

/// <summary>
/// Страница «Поиск» (ТФ-ПСК-01/02, ADR-0048): запрос из адреса выполняется при открытии (один раз, только интерактивно);
/// выдача — разделами со ссылками на место (карточки, носитель на моменте записи) и выделением совпадения; короткий
/// запрос — подсказка без обращения к серверу; «Найти» пишет запрос в адрес, повтор того же — ищет заново; найдено
/// больше предела — пометка. История (ADR-0049): недавние и закреплённые под строкой, при наборе — подсказки из них,
/// повтор — с видами и отделом запроса; закрепление, удаление и очистка — командами; быстрый поиск в шапке ведёт на
/// страницу «Поиск».
/// </summary>
public sealed class SearchPageTests : BunitContext, IAsyncLifetime
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly List<UnifiedSearchQuery> _queries = [];

    public SearchPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton(_mediator);
        var profile = Substitute.For<IProfile>();
        profile.DisplayName.Returns("СледствиеAI");
        Services.AddSingleton(profile);
        SetRendererInfo(new RendererInfo("Server", true));
        Respond(Result());
    }

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

    Task IAsyncLifetime.DisposeAsync() => base.DisposeAsync().AsTask();

    [Fact(DisplayName = "Запрос из адреса: поиск один раз с видами и отделом; строки ведут в карточки, фрагмент — на место записи; совпадение выделено")]
    public void Address_query_runs_once_and_renders_links()
    {
        Render<MudPopoverProvider>();
        Services.GetRequiredService<NavigationManager>().NavigateTo("/search?q=Иванов&kinds=Persons,Transcripts&direction=on");

        var cut = Render<Search>();

        cut.WaitForAssertion(() => _queries.Count.ShouldBe(1));
        _queries[0].ShouldBe(new UnifiedSearchQuery("Иванов", SearchKinds.Persons | SearchKinds.Transcripts, CaseDirection.Surveillance));

        var person = cut.Find("a[href='/persons/7']");
        person.QuerySelector("mark")!.TextContent.ShouldBe("Иванов");
        cut.FindAll("a[href='/cases/1']").Select(a => a.TextContent.Trim()).ShouldContain("№ 14/24");
        var moment = cut.Find("a[href='/media/assets/501?t=65000']");
        moment.TextContent.Trim().ShouldBe("[01:05]");
        cut.Markup.ShouldContain("Найдено по запросу «Иванов»: 2");
        cut.Markup.ShouldNotContain("показаны первые");
    }

    [Fact(DisplayName = "Без запроса в адресе — не ищет; короче двух знаков — подсказка без обращения к серверу; Enter пишет запрос в адрес")]
    public void Short_query_is_hinted_and_enter_navigates()
    {
        Render<MudPopoverProvider>();
        var cut = Render<Search>();
        _queries.ShouldBeEmpty();

        var input = cut.FindComponent<MudTextField<string>>().Find("input");
        input.Input("и");
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Найти").Click();
        cut.Markup.ShouldContain(UnifiedSearchQuery.TooShortMessage);
        _queries.ShouldBeEmpty();

        input = cut.FindComponent<MudTextField<string>>().Find("input");
        input.Input("01 KG 123");
        input.KeyDown(new KeyboardEventArgs { Key = "Enter" });

        Services.GetRequiredService<NavigationManager>().Uri.ShouldEndWith(new SearchPageState("01 KG 123").ToUri());
    }

    [Fact(DisplayName = "«Найти» с тем же запросом — ищет заново (новое обращение к материалам — новая запись журнала)")]
    public void Same_query_searches_again()
    {
        Render<MudPopoverProvider>();
        Services.GetRequiredService<NavigationManager>().NavigateTo("/search?q=Иванов");
        var cut = Render<Search>();
        cut.WaitForAssertion(() => _queries.Count.ShouldBe(1));

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Найти").Click();

        cut.WaitForAssertion(() => _queries.Count.ShouldBe(2));
        _queries[1].Kinds.ShouldBe(SearchKinds.All);
    }

    [Fact(DisplayName = "ADR-0055: телефоны — свой раздел (номер как записан, вид, фигурант, дело); у фигурантов — столбец «Гражданство»")]
    public void Phones_section_and_citizenship_column()
    {
        Respond(Result() with
        {
            Persons = [new PersonRow(7, 1, "Иванов Пётр", false, null, null, null, 1, 5, 0, 0, PersonRole.Target,
                new PersonQuestionnaire(Citizenship: "Казахстан"))],
            Phones = [new PhoneSearchRow(7, 1, "Иванов Пётр", new PersonPhoneRow(3, 7, PhoneKind.Mobile, "+996 555 123 456", "личный"))],
            Transcripts = [],
        });
        Render<MudPopoverProvider>();
        Services.GetRequiredService<NavigationManager>().NavigateTo("/search?q=0555 123 456");

        var cut = Render<Search>();

        cut.WaitForAssertion(() => cut.Markup.ShouldContain("+996 555 123 456"));
        var phones = cut.FindAll("table").Single(t => t.TextContent.Contains("+996 555 123 456", StringComparison.Ordinal));
        phones.QuerySelectorAll("th").Select(th => th.TextContent).ShouldBe(["Номер", "Вид", "Примечание", "Фигурант", "Дело"]);
        phones.TextContent.ShouldContain("Мобильный");
        phones.TextContent.ShouldContain("личный");
        phones.QuerySelector("a[href='/persons/7']").ShouldNotBeNull();
        var persons = cut.FindAll("table").Single(t => t.TextContent.Contains("Казахстан", StringComparison.Ordinal));
        persons.QuerySelectorAll("th").Select(th => th.TextContent).ShouldContain("Гражданство");
    }

    [Fact(DisplayName = "Найдено больше предела — «показаны первые»; ничего не найдено — так и сказано, без утверждения, что материалов нет")]
    public void Truncated_and_empty_results_are_explained()
    {
        Respond(Result() with { Truncated = SearchKinds.Persons });
        Render<MudPopoverProvider>();
        Services.GetRequiredService<NavigationManager>().NavigateTo("/search?q=Иванов");
        var cut = Render<Search>();
        cut.WaitForAssertion(() => cut.Markup.ShouldContain("показаны первые"));

        Respond(new UnifiedSearchResult("Жок", [], [], [], [], [], [], [], [], new Dictionary<int, string>(), SearchKinds.None));
        Services.GetRequiredService<NavigationManager>().NavigateTo("/search?q=Жок");
        var empty = Render<Search>();
        empty.WaitForAssertion(() => empty.Markup.ShouldContain("ничего не найдено — в пределах ваших дел и допуска"));
    }

    [Fact(DisplayName = "История: недавние под строкой (закреплённые первыми, со звездой); при наборе — только подходящие; щелчок — поиск с видами и отделом запроса")]
    public void History_chips_filter_and_repeat()
    {
        RespondHistory(
            new SearchHistoryRow(1, "01 KG 123 ABC", SearchKinds.Vehicles, CaseDirection.Surveillance, 1, true, DateTime.UtcNow),
            new SearchHistoryRow(2, "Пётр Иванов", SearchKinds.All, null, 4, false, DateTime.UtcNow.AddHours(-1)),
            new SearchHistoryRow(3, "Сидоров", SearchKinds.All, null, 0, false, DateTime.UtcNow.AddDays(-2)));
        Render<MudPopoverProvider>();
        var cut = Render<Search>();

        cut.WaitForAssertion(() => cut.FindAll(".search-history-chip").Count.ShouldBe(3));
        cut.Markup.ShouldContain("Недавние:");
        cut.FindAll(".search-history-chip")[0].TextContent.ShouldContain("01 KG 123 ABC");

        cut.FindComponent<MudTextField<string>>().Find("input").Input("ПЕТР");
        var chips = cut.FindAll(".search-history-chip");
        chips.Count.ShouldBe(1);
        chips[0].TextContent.ShouldContain("Пётр Иванов");
        cut.Markup.ShouldContain("Из истории:");

        cut.FindComponent<MudTextField<string>>().Find("input").Input(string.Empty);
        cut.FindAll(".search-history-chip")[0].Click();
        Services.GetRequiredService<NavigationManager>().Uri
            .ShouldEndWith(new SearchPageState("01 KG 123 ABC", SearchKinds.Vehicles, CaseDirection.Surveillance).ToUri());
    }

    [Fact(DisplayName = "Панель истории: закрепить, удалить и очистить — командами; после каждой история перечитывается; подпись — история видна только вам")]
    public void History_panel_sends_commands()
    {
        RespondHistory(
            new SearchHistoryRow(2, "Пётр Иванов", SearchKinds.Persons | SearchKinds.Vehicles, CaseDirection.Establishment, 4, false, DateTime.UtcNow));
        var sent = new List<object>();
        _mediator.Send(Arg.Do<PinSearchHistoryCommand>(sent.Add), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<bool>>(ResponseDto<bool>.Ok(true)));
        _mediator.Send(Arg.Do<DeleteSearchHistoryCommand>(sent.Add), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<bool>>(ResponseDto<bool>.Ok(true)));
        _mediator.Send(Arg.Do<ClearSearchHistoryCommand>(sent.Add), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<int>>(ResponseDto<int>.Ok(1)));
        Render<MudPopoverProvider>();
        var cut = Render<Search>();
        cut.WaitForAssertion(() => cut.FindAll(".search-history-chip").Count.ShouldBe(1));

        cut.FindAll("button").Single(b => b.TextContent.Contains("История (1)")).Click();
        var row = cut.Find(".search-history-row");
        row.TextContent.ShouldContain("фигуранты, автотранспорт · ОУ");
        row.TextContent.ShouldContain("найдено 4");
        cut.Markup.ShouldContain("История видна только вам");

        cut.Find("button[aria-label='Закрепить']").Click();
        cut.Find("button[aria-label='Удалить из истории']").Click();
        cut.FindAll("button").Single(b => b.TextContent.Contains("Очистить недавние")).Click();

        sent.ShouldBe([new PinSearchHistoryCommand(2, true), new DeleteSearchHistoryCommand(2), new ClearSearchHistoryCommand()]);
        _mediator.ReceivedCalls().Count(c => c.GetArguments()[0] is ListSearchHistoryQuery).ShouldBe(4); // открытие + после каждой команды
    }

    [Fact(DisplayName = "Быстрый поиск в шапке: Enter открывает «Поиск» с запросом; короткий запрос — просто страницу «Поиск»")]
    public void Quick_search_navigates_to_search_page()
    {
        var cut = Render<QuickSearch>();
        var navigation = Services.GetRequiredService<NavigationManager>();

        var input = cut.Find("input");
        input.Input("Иванов");
        input.KeyDown(new KeyboardEventArgs { Key = "Enter" });
        navigation.Uri.ShouldEndWith(new SearchPageState("Иванов").ToUri());

        input = cut.Find("input");
        input.Input("и");
        input.KeyDown(new KeyboardEventArgs { Key = "Enter" });
        navigation.Uri.ShouldEndWith("/search");
        _queries.ShouldBeEmpty(); // сам виджет ничего не ищет
    }

    private void RespondHistory(params SearchHistoryRow[] rows) =>
        _mediator.Send(Arg.Any<ListSearchHistoryQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<IReadOnlyList<SearchHistoryRow>>>(ResponseDto<IReadOnlyList<SearchHistoryRow>>.Ok(rows)));

    private void Respond(UnifiedSearchResult result) =>
        _mediator.Send(Arg.Do<UnifiedSearchQuery>(q => _queries.Add(q)), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<UnifiedSearchResult>>(ResponseDto<UnifiedSearchResult>.Ok(result)));

    private static UnifiedSearchResult Result() => new(
        "Иванов",
        [],
        [new PersonRow(7, 1, "Иванов Пётр", false, null, null, null, 1, 5, 0, 0, PersonRole.Target)],
        [],
        [],
        [],
        [],
        [new TranscriptSearchHit(501, 1, "запись.mp3", MediaKind.Audio, 3, 65_000, "говорит Иванов")],
        [],
        new Dictionary<int, string> { [1] = "14/24" },
        SearchKinds.None);
}
