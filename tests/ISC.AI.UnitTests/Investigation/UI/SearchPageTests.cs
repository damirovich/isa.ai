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
/// больше предела — пометка.
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

    [Fact(DisplayName = "Найдено больше предела — «показаны первые»; ничего не найдено — так и сказано, без утверждения, что материалов нет")]
    public void Truncated_and_empty_results_are_explained()
    {
        Respond(Result() with { Truncated = SearchKinds.Persons });
        Render<MudPopoverProvider>();
        Services.GetRequiredService<NavigationManager>().NavigateTo("/search?q=Иванов");
        var cut = Render<Search>();
        cut.WaitForAssertion(() => cut.Markup.ShouldContain("показаны первые"));

        Respond(new UnifiedSearchResult("Жок", [], [], [], [], [], [], [], new Dictionary<int, string>(), SearchKinds.None));
        Services.GetRequiredService<NavigationManager>().NavigateTo("/search?q=Жок");
        var empty = Render<Search>();
        empty.WaitForAssertion(() => empty.Markup.ShouldContain("ничего не найдено — в пределах ваших дел и допуска"));
    }

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
        [new TranscriptSearchHit(501, 1, "запись.mp3", MediaKind.Audio, 3, 65_000, "говорит Иванов")],
        [],
        new Dictionary<int, string> { [1] = "14/24" },
        SearchKinds.None);
}
