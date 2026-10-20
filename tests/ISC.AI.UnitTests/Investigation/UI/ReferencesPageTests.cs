using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AngleSharp.Dom;
using Bunit;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Profiles;
using ISC.AI.Profile.Investigation.Application.Features.References;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using ISC.AI.Profile.Investigation.UI;
using Mediator;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Investigation.UI;

/// <summary>
/// Страница «Справочники» (ТФ-АДМ-07): список вкладки — постранично (по умолчанию 10 записей, как реестр дел), смена
/// поиска возвращает на первую страницу; «Изменить» и «Выключить…» — в одной строке, строка таблицы не вырастает.
/// </summary>
public sealed class ReferencesPageTests : BunitContext, IAsyncLifetime
{
    private const int CountryCount = 23;

    private readonly IMediator _mediator = Substitute.For<IMediator>();

    public ReferencesPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton(_mediator);
        var profile = Substitute.For<IProfile>();
        profile.DisplayName.Returns("СледствиеAI");
        Services.AddSingleton(profile);
        SetRendererInfo(new RendererInfo("Server", true));

        // «Кыргызстан» первым (порядок 1), дальше 22 страны с одинаковым порядком — по алфавиту; плюс другая вкладка.
        IReadOnlyList<ReferenceItemRow> rows =
        [
            new(1, ReferenceKind.Country, "Кыргызстан", null, 1, true),
            .. Enumerable.Range(1, CountryCount - 1)
                .Select(i => new ReferenceItemRow(100 + i, ReferenceKind.Country, $"Страна {i:00}", null, 10, true)),
            new(500, ReferenceKind.Rank, "майор", null, 0, true),
        ];
        _mediator.Send(Arg.Any<ListReferenceItemsQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<IReadOnlyList<ReferenceItemRow>>>(ResponseDto<IReadOnlyList<ReferenceItemRow>>.Ok(rows)));
    }

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

    Task IAsyncLifetime.DisposeAsync() => base.DisposeAsync().AsTask();

    [Fact(DisplayName = "Справочник длиннее страницы — по 10 записей, «страница 1 из 3»; переход на страницу 2 — следующие записи")]
    public void Long_reference_is_paged()
    {
        var cut = RenderCountries();

        Names(cut).ShouldBe(["Кыргызстан", .. Enumerable.Range(1, 9).Select(i => $"Страна {i:00}")]);
        References.DefaultPageSize.ShouldBe(10);
        cut.Markup.ShouldContain($"Записей: {CountryCount}, действующих: {CountryCount} · страница 1 из 3");

        PageButton(cut, "2").Click();

        cut.WaitForAssertion(() => Names(cut).ShouldBe(Enumerable.Range(10, 10).Select(i => $"Страна {i:00}")));
        cut.Markup.ShouldContain("страница 2 из 3");
        PageButton(cut, "3").Click();
        cut.WaitForAssertion(() => Names(cut).Count.ShouldBe(CountryCount - 20));
    }

    [Fact(DisplayName = "Поиск по справочнику возвращает на первую страницу отбора")]
    public void Search_resets_to_first_page()
    {
        var cut = RenderCountries();
        PageButton(cut, "3").Click();
        cut.WaitForAssertion(() => cut.Markup.ShouldContain("страница 3 из 3"));

        cut.Find("input[placeholder='Найти по наименованию или коду']").Input("Страна 1");

        // «Страна 1» — «Страна 10»…«Страна 19»: 10 записей, одна страница.
        cut.WaitForAssertion(() => Names(cut).ShouldBe(Enumerable.Range(10, 10).Select(i => $"Страна {i:00}")));
        cut.Markup.ShouldContain("страница 1 из 1");
    }

    [Fact(DisplayName = "«Изменить» и «Выключить…» — в одной строке ячейки (без переноса), столбец по ширине кнопок")]
    public void Row_actions_stay_on_one_line()
    {
        var cut = RenderCountries();

        var actions = cut.FindAll("td[data-label='Действия']")[0];
        actions.GetAttribute("style").ShouldNotBeNull().ShouldContain("white-space:nowrap");
        var line = actions.QuerySelector("div.flex-nowrap").ShouldNotBeNull();
        line.QuerySelectorAll("button").Select(b => b.TextContent.Trim()).ShouldBe(["Изменить", "Выключить…"]);
    }

    private IRenderedComponent<References> RenderCountries()
    {
        Render<MudPopoverProvider>();
        Services.GetRequiredService<NavigationManager>().NavigateTo("/admin/references");
        var cut = Render<References>();
        cut.WaitForAssertion(() => cut.FindAll(".mud-tab").ShouldContain(t => t.TextContent.Contains("Страны", StringComparison.Ordinal)));
        cut.FindAll(".mud-tab").Single(t => t.TextContent.Contains("Страны", StringComparison.Ordinal)).Click();
        cut.WaitForAssertion(() => Names(cut).ShouldContain("Кыргызстан"));
        return cut;
    }

    private static List<string> Names(IRenderedComponent<References> cut) =>
        cut.FindAll("td[data-label='Наименование']").Select(td => td.TextContent.Trim()).ToList();

    private static IElement PageButton(IRenderedComponent<References> cut, string number) =>
        cut.FindAll(".mud-pagination button").Single(b => b.TextContent.Trim() == number);
}
