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
        StubList(new CasePage(
        [
            Row(1, "ОН-1", CaseDirection.Surveillance),
            Row(2, "П-1", direction: null),
        ], 2));
    }

    private void StubList(CasePage page) =>
        _mediator.Send(Arg.Do<ListCasesQuery>(q => _listQueries.Add(q)), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<CasePage>>(ResponseDto<CasePage>.Ok(page)));

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

    Task IAsyncLifetime.DisposeAsync() => base.DisposeAsync().AsTask();

    private void Scope(IReadOnlyList<DivisionNode> owner, params CaseDirection[] directions) =>
        RespondOk<GetAccessScopeQuery, AccessScope>(new AccessScope(2, owner, InvestigationRole.Investigator, 7, directions));

    [Fact(DisplayName = "Стартовая страница: «Оперативно-поисковое управление»; ОН в допуске — кнопка, ОУ и ОТМ — «Нет в вашем допуске»")]
    public void Home_enables_only_departments_in_clearance()
    {
        Scope([OnGroup], CaseDirection.Surveillance);

        var cut = Render<InvestigationHome>();

        cut.Markup.ShouldContain(InvestigationHome.OrganizationName);
        cut.FindAll("button.home-tile").Select(b => b.QuerySelector(".home-tile-code")!.TextContent).ShouldBe(["ОН"]);
        var off = cut.FindAll(".home-tile-off");
        off.Select(t => t.QuerySelector(".home-tile-code")!.TextContent).ShouldBe(["ОУ", "ОТМ"]);
        off.ShouldAllBe(t => t.TagName == "DIV"); // не кнопка и не ссылка
        off.ShouldAllBe(t => t.TextContent.Contains("Нет в вашем допуске"));
        cut.Markup.ShouldNotContain("Вход с рабочих мест контура ОТМ");
        cut.FindAll("a").Select(a => a.GetAttribute("href")).ShouldBe(["/cases"]);
    }

    [Fact(DisplayName = "ОТМ (решение от 12.10.2026): подразделение ОТМ в допуске — плитка ОТМ активна и открывает /cases?direction=otm")]
    public void Otm_tile_works_by_clearance_like_others()
    {
        Scope([OnGroup], CaseDirection.Technical);
        var cut = Render<InvestigationHome>();

        cut.FindAll("button.home-tile").Select(b => b.QuerySelector(".home-tile-code")!.TextContent).ShouldBe(["ОТМ"]);
        cut.FindAll(".home-tile-off").Select(t => t.QuerySelector(".home-tile-code")!.TextContent).ShouldBe(["ОН", "ОУ"]);

        cut.Find("button.home-tile").Click();

        Services.GetRequiredService<NavigationManager>().Uri.ShouldEndWith("/cases?direction=otm");
    }

    [Fact(DisplayName = "Плитка ОН открывает список дел с отбором по отделу: /cases?direction=on")]
    public void Tile_opens_cases_filtered_by_department()
    {
        Scope([OnGroup], CaseDirection.Surveillance);
        var cut = Render<InvestigationHome>();

        cut.Find("button.home-tile").Click();

        Services.GetRequiredService<NavigationManager>().Uri.ShouldEndWith("/cases?direction=on");
    }

    [Fact(DisplayName = "Подразделения допуска не отнесены к отделам — все три плитки неактивны и есть подсказка обратиться к администратору")]
    public void Home_explains_unmarked_divisions()
    {
        Scope([Root]);

        var cut = Render<InvestigationHome>();

        cut.FindAll("button.home-tile").ShouldBeEmpty();
        // Текст подсказки переносится строкой — сравниваем с пробелами, как показывает браузер.
        System.Text.RegularExpressions.Regex.Replace(cut.Markup, @"\s+", " ").ShouldContain("не отнесены ни к одному отделу — ОН, ОУ или ОТМ");
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
        cells[1].QuerySelector("span")!.GetAttribute("title")!.ShouldContain("не отнесено ни к одному отделу");
    }

    [Fact(DisplayName = "Реестр дел: по 10 на страницу, по умолчанию — по статусу; щелчок по «Открыто» — по дате с 1-й страницы, повторный — в обратную сторону")]
    public void Cases_list_pages_by_ten_and_sorts_on_server()
    {
        Render<MudPopoverProvider>();
        var cut = Render<Cases>();
        cut.WaitForAssertion(() => _listQueries.ShouldNotBeEmpty());

        var first = _listQueries[^1];
        first.PageSize.ShouldBe(Cases.PageSize);
        Cases.PageSize.ShouldBe(10);
        first.Sort.ShouldBe(CaseSortField.Status);
        first.SortDescending.ShouldBeFalse();

        // Порядок строк — как отдал сервер: страница сама не пересортировывает.
        cut.FindAll("td[data-label='Номер']").Select(c => c.TextContent.Trim()).ShouldBe(["ОН-1", "П-1"]);

        SortHeader(cut, "Открыто").Click();
        cut.WaitForAssertion(() => _listQueries[^1].Sort.ShouldBe(CaseSortField.OpenedAt));
        _listQueries[^1].SortDescending.ShouldBeFalse();
        _listQueries[^1].Page.ShouldBe(1);

        SortHeader(cut, "Открыто").Click();
        cut.WaitForAssertion(() => _listQueries[^1].SortDescending.ShouldBeTrue());
    }

    private static AngleSharp.Dom.IElement SortHeader(IRenderedComponent<Cases> cut, string text) =>
        cut.FindAll(".mud-table-sort-label").Single(e => e.TextContent.Trim() == text);

    [Fact(DisplayName = "Реестр дел: состояние из адреса — статус, страница, размер, порядок и «только мои» (сотрудник из допуска) уходят в запрос")]
    public void Cases_list_restores_state_from_address()
    {
        Scope([OnGroup], CaseDirection.Surveillance);
        Render<MudPopoverProvider>();
        Services.GetRequiredService<NavigationManager>().NavigateTo("/cases?status=Closed&page=2&size=25&sort=OpenedAt&desc=1&mine=1&q=14");

        var cut = Render<Cases>();

        cut.WaitForAssertion(() => _listQueries.ShouldNotBeEmpty());
        var query = _listQueries[^1];
        query.Status.ShouldBe(CaseStatus.Closed);
        query.Page.ShouldBe(2);
        query.PageSize.ShouldBe(25);
        query.Sort.ShouldBe(CaseSortField.OpenedAt);
        query.SortDescending.ShouldBeTrue();
        query.InvestigatorUserId.ShouldBe(7); // AccessScope.UserId
        query.Text.ShouldBe("14");
        _listQueries.Count.ShouldBe(1); // одно открытие — один запрос (ТБ-030)
    }

    [Fact(DisplayName = "Реестр дел: вкладки статуса со счётчиками; щелчок по вкладке — отбор по статусу с 1-й страницы и запись в адрес")]
    public void Cases_list_status_tabs_show_counts_and_filter()
    {
        StubList(new CasePage([Row(1, "ОН-1", CaseDirection.Surveillance)], 7,
            new Dictionary<CaseStatus, int> { [CaseStatus.InProgress] = 5, [CaseStatus.Suspended] = 2 }));
        Render<MudPopoverProvider>();
        var cut = Render<Cases>();
        cut.WaitForAssertion(() => _listQueries.ShouldNotBeEmpty());

        var tabs = cut.FindAll(".mud-toggle-item").Select(t => t.TextContent.Trim()).ToList();
        tabs.ShouldBe(["Все (7)", "В производстве (5)", "Приостановлено (2)", "Закрыто (0)"]);

        cut.FindAll(".mud-toggle-item").Single(t => t.TextContent.Contains("Приостановлено")).Click();

        cut.WaitForAssertion(() => _listQueries[^1].Status.ShouldBe(CaseStatus.Suspended));
        _listQueries[^1].Page.ShouldBe(1);
        Services.GetRequiredService<NavigationManager>().Uri.ShouldEndWith("/cases?status=Suspended");
    }

    [Fact(DisplayName = "Строка реестра: номер — ссылка на дело (открывается и в новой вкладке), повтор номера задания убран, «Ведёт» — имя сотрудника")]
    public void Cases_row_links_number_and_shows_investigator()
    {
        RespondOk<ListUserDirectoryQuery, IReadOnlyList<UserAccountRow>>(
            [new UserAccountRow(2, "ivanov", "Иванов И.И.", IsActive: false, HasLocalPassword: true, MustChangePassword: false)]);
        StubList(new CasePage(
        [
            Row(1, "14/24", null) with { Title = "Задание № 14/24 — Учебный Марат", TaskNumber = "14/24", InvestigatorUserId = 2 },
        ], 1));
        Render<MudPopoverProvider>();
        var cut = Render<Cases>();

        cut.WaitForAssertion(() => cut.FindAll("td[data-label='Номер'] a").Count.ShouldBe(1));
        cut.Find("td[data-label='Номер'] a").GetAttribute("href").ShouldBe("/cases/1");
        cut.Find("td[data-label='Номер']").TextContent.ShouldNotContain("задание №");
        var title = cut.Find("td[data-label='Название'] span");
        title.TextContent.Trim().ShouldBe("Учебный Марат");
        title.GetAttribute("title").ShouldBe("Задание № 14/24 — Учебный Марат");
        cut.Find("td[data-label='Ведёт']").TextContent.Trim().ShouldBe("Иванов И.И."); // и отключённый — его дела остаются
    }

    [Fact(DisplayName = "Выгрузка в Excel: запрос выгрузки с текущими отборами и порядком, файл .csv отдаётся браузеру")]
    public void Cases_export_downloads_csv_with_current_filters()
    {
        RespondOk<ExportCasesQuery, CasesExport>(new CasesExport([Row(1, "ОН-1", CaseDirection.Surveillance)], 1, "Петров П.П."));
        ExportCasesQuery? sent = null;
        _mediator.When(m => m.Send(Arg.Any<ExportCasesQuery>(), Arg.Any<CancellationToken>()))
            .Do(c => sent = c.Arg<ExportCasesQuery>());
        Render<MudPopoverProvider>();
        Services.GetRequiredService<NavigationManager>().NavigateTo("/cases?kind=ObjectTask&sort=Number");
        var cut = Render<Cases>();
        cut.WaitForAssertion(() => _listQueries.ShouldNotBeEmpty());

        cut.FindAll("button").Single(b => b.TextContent.Contains("Выгрузить в Excel")).Click();

        cut.WaitForAssertion(() => JSInterop.Invocations["downloadFileFromStream"].Count.ShouldBe(1));
        sent.ShouldNotBeNull();
        sent.Kind.ShouldBe(CaseKind.ObjectTask);
        sent.Sort.ShouldBe(CaseSortField.Number);
        sent.SortDescending.ShouldBeFalse();
        JSInterop.Invocations["downloadFileFromStream"][0].Arguments[0]!.ToString()!.ShouldStartWith("Реестр дел ");
        JSInterop.Invocations["downloadFileFromStream"][0].Arguments[0]!.ToString()!.ShouldEndWith(".csv");
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
