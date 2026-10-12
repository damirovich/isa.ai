using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Admin.Application.Features.Accounts;
using ISC.AI.Modules.Admin.Application.Features.Clearances;
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
/// Подразделения в допуске деревом (ТБ-011/020): отступ вместо склеенных «Управление / Отдел», метка отдела профиля,
/// «+ вложенные» для ветки — потому что отметка у управления НЕ открывает его отделы.
/// </summary>
public sealed class ClearanceDivisionTreeTests : BunitContext, IAsyncLifetime
{
    // 7Управление → Отдел наблюдения (ОН) → Группа наблюдения 1 (ОН по вышестоящему); 7Управление → Отдел установки (ОУ).
    private static readonly ClearanceDivision Department = new(1, "7Управление", null, "7Управление");
    private static readonly ClearanceDivision Surveillance = new(2, "7Управление / Отдел наблюдения", 1, "Отдел наблюдения", "ОН");
    private static readonly ClearanceDivision Group = new(3, "Отдел наблюдения / Группа наблюдения 1", 2, "Группа наблюдения 1", "ОН");
    private static readonly ClearanceDivision Establishment = new(4, "7Управление / Отдел установки", 1, "Отдел установки", "ОУ");

    private readonly IMediator _mediator = Substitute.For<IMediator>();

    public ClearanceDivisionTreeTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton(_mediator);
        SetRendererInfo(new RendererInfo("Server", true));
    }

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

    Task IAsyncLifetime.DisposeAsync() => base.DisposeAsync().AsTask();

    [Fact(DisplayName = "Дерево: родитель, под ним вложенные по наименованию; уровень и все вложенные ветки")]
    public void Tree_orders_parent_then_children_with_depth()
    {
        var rows = ClearanceDivisionTree.Build([Establishment, Group, Department, Surveillance]);

        rows.Select(r => (r.Title, r.Depth)).ShouldBe(
        [
            ("7Управление", 0),
            ("Отдел наблюдения", 1),
            ("Группа наблюдения 1", 2),
            ("Отдел установки", 1),
        ]);
        rows[0].DescendantIds.ShouldBe([2, 4, 3], ignoreOrder: true);
        rows[1].DescendantIds.ShouldBe([3]);
        rows[2].HasChildren.ShouldBeFalse();
    }

    [Fact(DisplayName = "Дерево: родителя нет в справочнике — строка на верхнем уровне; цикл не зацикливает построение")]
    public void Orphans_and_cycles_are_safe()
    {
        var orphan = new ClearanceDivision(9, "Пропавший / Сирота", 99, "Сирота");
        var a = new ClearanceDivision(5, "Б / А", 6, "А");
        var b = new ClearanceDivision(6, "А / Б", 5, "Б");

        var rows = ClearanceDivisionTree.Build([orphan, a, b]);

        rows.Select(r => r.Title).ShouldBe(["Сирота", "А", "Б"]);
        rows.Single(r => r.Title == "Сирота").Depth.ShouldBe(0);
        rows.Count.ShouldBe(3);
    }

    [Fact(DisplayName = "Поиск: видна найденная строка и цепочка над ней, остальное скрыто")]
    public void Search_keeps_matches_and_their_ancestors()
    {
        var rows = ClearanceDivisionTree.Build([Department, Surveillance, Group, Establishment]);

        ClearanceDivisionTree.Visible(rows, "группа").Select(r => r.Title)
            .ShouldBe(["7Управление", "Отдел наблюдения", "Группа наблюдения 1"]);
        ClearanceDivisionTree.Visible(rows, "  ").Count.ShouldBe(4);
    }

    [Fact(DisplayName = "Карточка сотрудника: подразделения деревом с отступом и меткой отдела, без склеенных «Управление / Отдел»")]
    public void User_card_shows_divisions_as_tree()
    {
        var cut = RenderCard();

        var rows = cut.FindAll(".clearance-row");
        rows.Select(r => r.QuerySelector(".mud-checkbox")!.TextContent.Trim())
            .ShouldBe(["7Управление", "Отдел наблюдения", "Группа наблюдения 1", "Отдел установки"]);
        rows.Select(r => r.GetAttribute("style")!.Split(';')[0]).ShouldBe(
            ["padding-left:0px", "padding-left:24px", "padding-left:48px", "padding-left:24px"]);
        cut.Find(".clearance-tree").TextContent.ShouldNotContain(" / ");
        cut.FindAll(".clearance-badge").Select(b => b.TextContent.Trim()).ShouldBe(["ОН", "ОН", "ОУ"]);
        // Текст подсказки в разметке переносится строкой — браузер показывает перенос пробелом; сравниваем так же.
        System.Text.RegularExpressions.Regex.Replace(cut.Markup, @"\s+", " ")
            .ShouldContain("Галочка у управления или отдела не открывает вложенные в него");
        cut.Markup.ShouldContain("отмечено 1 из 4");
    }

    [Fact(DisplayName = "«+ вложенные» у управления отмечает всю ветку; повторно — «− вложенные» снимает вложенные, само остаётся")]
    public void Branch_button_toggles_descendants()
    {
        var cut = RenderCard();

        cut.FindAll(".clearance-row")[0].QuerySelector(".clearance-branch")!.Click();

        cut.Markup.ShouldContain("отмечено 4 из 4");
        cut.Markup.ShouldContain("Есть несохранённые изменения допуска");
        cut.FindAll(".clearance-row")[0].QuerySelector(".clearance-branch")!.TextContent.Trim().ShouldBe("− вложенные");

        cut.FindAll(".clearance-row")[0].QuerySelector(".clearance-branch")!.Click();

        cut.Markup.ShouldContain("отмечено 1 из 4");
    }

    private IRenderedComponent<UserCard> RenderCard()
    {
        var card = new UserAccountCard(
            new UserAccountView(
                new UserAccountRow(1, "admin", "Администратор системы", IsActive: true, HasLocalPassword: true,
                    MustChangePassword: false, MaxClassification: 4, Divisions: [1]),
                "Administrator", "Администратор"),
            [new RoleOption("Administrator", "Администратор")],
            [Department, Surveillance, Group, Establishment],
            [Department],
            IsSelf: true,
            IsInitialSetup: false);
        _mediator.Send(Arg.Any<GetUserAccountQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<UserAccountCard>>(ResponseDto<UserAccountCard>.Ok(card)));
        _mediator.Send(Arg.Any<GetUserHistoryQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<IReadOnlyList<UserHistoryEntry>>>(
                ResponseDto<IReadOnlyList<UserHistoryEntry>>.Ok(Array.Empty<UserHistoryEntry>())));

        Render<MudPopoverProvider>();
        return Render<UserCard>(p => p.Add(c => c.UserId, 1));
    }
}
