using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Profiles;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Application.Features.Cases;
using ISC.AI.Profile.Investigation.Application.Features.Directory;
using ISC.AI.Profile.Investigation.Application.Features.Divisions;
using ISC.AI.Profile.Investigation.Application.Features.References;
using ISC.AI.Profile.Investigation.Application.Features.Scope;
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
/// Отделы ОН/ОУ (ТЭ-008, ADR-0039) на экранах: стартовая страница — плитка отдела активна, только если его
/// подразделение есть в допуске, ОТМ — надпись без перехода; плитка открывает список с отбором по отделу; в списке
/// отдел дела — по подразделению; в форме нового дела единственное подразделение допуска подставляется само и
/// показывает свой отдел; справочник подразделений показывает свою и унаследованную отметку.
/// </summary>
public sealed class CaseDirectionUiTests : BunitContext, IAsyncLifetime
{
    private static readonly DivisionNode Root = new(1, "ОПУ", null, null, true, 0);
    private static readonly DivisionNode OnDepartment = new(2, "Отдел ОН", null, 1, true, 1, CaseDirection.Surveillance);
    private static readonly DivisionNode OnGroup = new(3, "Группа ОН-1", null, 2, true, 1);

    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly List<ListCasesQuery> _listQueries = [];

    public CaseDirectionUiTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton(_mediator);
        var profile = Substitute.For<IProfile>();
        profile.DisplayName.Returns("СледствиеAI");
        Services.AddSingleton(profile);
        SetRendererInfo(new RendererInfo("Server", true));

        RespondOk<ListDivisionsQuery, IReadOnlyList<DivisionNode>>([Root, OnDepartment, OnGroup]);
        RespondOk<ListReferenceItemsQuery, IReadOnlyList<ReferenceItemRow>>([]);
        RespondOk<ListUserDirectoryQuery, IReadOnlyList<UserAccountRow>>([]);
        _mediator.Send(Arg.Do<ListCasesQuery>(q => _listQueries.Add(q)), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<CasePage>>(ResponseDto<CasePage>.Ok(new CasePage(
            [
                Row(1, "ОН-1", CaseDirection.Surveillance),
                Row(2, "П-1", direction: null),
            ], 2))));
    }

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

    Task IAsyncLifetime.DisposeAsync() => base.DisposeAsync().AsTask();

    private void Scope(IReadOnlyList<DivisionNode> owner, params CaseDirection[] directions) =>
        RespondOk<GetAccessScopeQuery, AccessScope>(new AccessScope(2, owner, InvestigationRole.Investigator, 7, directions));

    [Fact(DisplayName = "Стартовая страница: «Оперативно-поисковое управление»; ОН в допуске — кнопка, ОУ — «Нет в вашем допуске», ОТМ — без перехода")]
    public void Home_enables_only_departments_in_clearance()
    {
        Scope([OnGroup], CaseDirection.Surveillance);

        var cut = Render<InvestigationHome>();

        cut.Markup.ShouldContain(InvestigationHome.OrganizationName);
        cut.FindAll("button.home-tile").Select(b => b.QuerySelector(".home-tile-code")!.TextContent).ShouldBe(["ОН"]);
        var off = cut.FindAll(".home-tile-off");
        off.Select(t => t.QuerySelector(".home-tile-code")!.TextContent).ShouldBe(["ОУ", "ОТМ"]);
        off.ShouldAllBe(t => t.TagName == "DIV"); // не кнопка и не ссылка
        off[0].TextContent.ShouldContain("Нет в вашем допуске");
        off[1].TextContent.ShouldContain("Вход с рабочих мест контура ОТМ");
        // Единственная ссылка — «Все доступные дела»: в контур ОТМ страница не ведёт.
        cut.FindAll("a").Select(a => a.GetAttribute("href")).ShouldBe(["/cases"]);
    }

    [Fact(DisplayName = "Плитка ОН открывает список дел с отбором по отделу: /cases?direction=on")]
    public void Tile_opens_cases_filtered_by_department()
    {
        Scope([OnGroup], CaseDirection.Surveillance);
        var cut = Render<InvestigationHome>();

        cut.Find("button.home-tile").Click();

        Services.GetRequiredService<NavigationManager>().Uri.ShouldEndWith("/cases?direction=on");
    }

    [Fact(DisplayName = "Подразделения допуска не отнесены к отделам — обе плитки неактивны и есть подсказка обратиться к администратору")]
    public void Home_explains_unmarked_divisions()
    {
        Scope([Root]);

        var cut = Render<InvestigationHome>();

        cut.FindAll("button.home-tile").ShouldBeEmpty();
        cut.Markup.ShouldContain("не отнесены ни к ОН, ни к ОУ");
    }

    [Fact(DisplayName = "Список дел: отбор по отделу из адреса (?direction=ou); отдел дела — по подразделению, неотмеченное — прочерк")]
    public void Cases_list_takes_department_from_address()
    {
        Render<MudPopoverProvider>();
        Services.GetRequiredService<NavigationManager>().NavigateTo("/cases?direction=ou");

        var cut = Render<Cases>();

        cut.WaitForAssertion(() => _listQueries.ShouldNotBeEmpty());
        _listQueries[^1].Direction.ShouldBe(CaseDirection.Establishment);
        var cells = cut.FindAll("td[data-label='Отдел']");
        cells[0].TextContent.Trim().ShouldBe("ОН");
        cells[1].TextContent.Trim().ShouldBe("—");
        cells[1].QuerySelector("span")!.GetAttribute("title")!.ShouldContain("не отнесено к ОН или ОУ");
    }

    [Fact(DisplayName = "Новое дело: единственное подразделение допуска подставлено само и показывает отдел «ОН» (по вышестоящему)")]
    public void New_case_gets_division_and_department_automatically()
    {
        Scope([OnGroup], CaseDirection.Surveillance);
        Render<MudPopoverProvider>();
        var dialogs = Render<MudDialogProvider>();
        var cut = Render<Cases>();

        cut.FindAll("button").First(b => b.TextContent.Contains("Новое дело")).Click();

        // Встроенный диалог рисует поставщик диалогов MudBlazor — подсказку ищем в его разметке.
        dialogs.WaitForAssertion(() => dialogs.Markup.ShouldContain("Отдел: ОН — Оперативное наблюдение"));
        dialogs.Markup.ShouldNotContain("Заполните: номер, подразделение");
    }

    [Fact(DisplayName = "Справочник подразделений: своя отметка отдела — залитый чип, унаследованная — контурный с пояснением")]
    public void Divisions_show_own_and_inherited_department()
    {
        Render<MudPopoverProvider>();

        var cut = Render<Divisions>();

        cut.WaitForAssertion(() => cut.FindAll(".division-direction").Count.ShouldBe(2));
        var chips = cut.FindAll(".division-direction");
        chips.Select(c => c.TextContent.Trim()).ShouldBe(["ОН", "ОН"]);
        chips[0].ClassList.ShouldContain("mud-chip-filled");
        chips[1].ClassList.ShouldContain("mud-chip-outlined");
        chips[1].GetAttribute("title")!.ShouldContain("по вышестоящему");
    }

    private static CaseRow Row(int id, string number, CaseDirection? direction) => new(
        id, number, "Дело " + number, CaseKind.CriminalCase, CaseStatus.InProgress, new DateOnly(2026, 9, 1),
        InvestigatorUserId: null, DivisionId: 3, Classification: 0, MediaCount: 0, PersonCount: 0, Direction: direction);

    private void RespondOk<TRequest, TData>(TData data)
        where TRequest : IRequest<ResponseDto<TData>> =>
        _mediator.Send(Arg.Any<TRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<TData>>(ResponseDto<TData>.Ok(data)));
}
