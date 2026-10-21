using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using ISC.AI.Abstractions.Application;
using ISC.AI.Profile.Investigation.Application.Features.LinkEvents;
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
/// Вкладка «События» карточки фигуранта (ТФ-ПЕР-11, ADR-0056): участие объекта (уровень, роль, транспорт и адрес,
/// отметка поиска), другие участники — доступный ссылкой на карточку, чужой — только номером, видом и статусом дела
/// (ТБ-084); действия в одной строке; граф события; связать нельзя без второго объекта.
/// </summary>
public sealed class PersonEventLinksTests : BunitContext, IAsyncLifetime
{
    private const int PersonId = 7;

    private readonly IMediator _mediator = Substitute.For<IMediator>();

    public PersonEventLinksTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton(_mediator);
        SetRendererInfo(new RendererInfo("Server", true));

        IReadOnlyList<LinkEventRow> events =
        [
            new LinkEventRow(5, LinkEventKind.Meeting, "Встреча у рынка", new DateOnly(2026, 10, 5), "обсуждали поставку", 2, 5, 42,
                DateTime.UtcNow,
                [
                    new LinkEventParticipantRow(1, PersonId, "Иванов Иван", null, 3, "12-2026", CaseKind.ObjectTask, CaseStatus.InProgress, 42,
                        true, "водитель", LinkLevel.Probable, true, "01KG123ABC", "Toyota", "г. Ош, ул. Ленина, 1", 1),
                    new LinkEventParticipantRow(2, 8, "Петров Пётр", null, 4, "13-2026", CaseKind.ObjectTask, CaseStatus.InProgress, 42,
                        true, "пассажир", LinkLevel.Confirmed, true, null, null, null, 1),
                    new LinkEventParticipantRow(3, null, null, null, 9, "77/26", CaseKind.CriminalCase, CaseStatus.Suspended, 50,
                        false, null, LinkLevel.Confirmed, false, null, null, null, 2),
                ]),
        ];
        _mediator.Send(Arg.Any<ListPersonEventsQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<IReadOnlyList<LinkEventRow>>>(ResponseDto<IReadOnlyList<LinkEventRow>>.Ok(events)));
    }

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

    Task IAsyncLifetime.DisposeAsync() => base.DisposeAsync().AsTask();

    [Fact(DisplayName = "Событие: участие объекта, доступный участник — ссылкой, чужой — номером дела без ФИО (ТБ-084); число — на вкладку")]
    public void Event_row_shows_participation_and_others()
    {
        var count = -1;
        Render<MudPopoverProvider>();
        var cut = Render<PersonEventLinks>(p => p
            .Add(x => x.PersonId, PersonId)
            .Add(x => x.CountChanged, (int value) => count = value));

        cut.WaitForAssertion(() => cut.Markup.ShouldContain("Встреча у рынка"));
        count.ShouldBe(1);
        cut.Find("td[data-label='Дата']").TextContent.Trim().ShouldBe("05.10.2026");

        var own = cut.Find("td[data-label='Участие объекта']").TextContent;
        own.ShouldContain("предположительно");
        own.ShouldContain("водитель");
        own.ShouldContain("транспорт: 01KG123ABC Toyota");
        own.ShouldContain("адрес контакта: г. Ош, ул. Ленина, 1");
        own.ShouldContain("учитывается в общем поиске");

        var others = cut.Find("td[data-label='Другие участники']");
        others.QuerySelector("a[href='/persons/8']").ShouldNotBeNull().TextContent.ShouldBe("Петров Пётр");
        others.TextContent.ShouldContain("дело № 77/26");
        others.TextContent.ShouldContain(CaseStatus.Suspended.Label());
        others.TextContent.ShouldNotContain("Иванов Иван"); // сам объект — не «другой участник»

        var actions = cut.Find("td[data-label='Действия'] div.flex-nowrap");
        actions.QuerySelectorAll("a, button").Select(b => b.TextContent.Trim()).ShouldBe(["Изменить", "Граф", "Исключить…"]);
        actions.QuerySelector($"a[href='/events/5/graph?from={PersonId}']").ShouldNotBeNull();
    }

    [Fact(DisplayName = "«Связать через событие»: без второго объекта — подсказка и кнопка «Связать» недоступна; новое событие по умолчанию")]
    public void Link_requires_other_object()
    {
        Render<MudPopoverProvider>();
        var dialogs = Render<MudDialogProvider>();
        var cut = Render<PersonEventLinks>(p => p.Add(x => x.PersonId, PersonId));
        cut.WaitForAssertion(() => cut.Markup.ShouldContain("Встреча у рынка"));

        cut.FindAll("button").Single(b => b.TextContent.Contains("Связать через событие", StringComparison.Ordinal)).Click();

        dialogs.WaitForAssertion(() => dialogs.Markup.ShouldContain("Выберите второй объект"));
        dialogs.FindAll("button").Single(b => b.TextContent.Trim() == "Связать").HasAttribute("disabled").ShouldBeTrue();
        dialogs.Markup.ShouldContain("Учитывать в общем поиске");
        dialogs.Markup.ShouldContain("Название *"); // событие по умолчанию — новое

        // Поля транспорта и адреса нового участия пустые — не имена переменных формы (ошибка 21.10.2026).
        dialogs.Markup.ShouldNotContain("_plate");
        dialogs.Markup.ShouldNotContain("_vehicle");
        dialogs.Markup.ShouldNotContain("_address");
    }

    [Fact(DisplayName = "«Изменить»: в полях — сведения участия объекта (госномер, марка, адрес контакта, роль) и события")]
    public void Edit_dialog_is_prefilled()
    {
        Render<MudPopoverProvider>();
        var dialogs = Render<MudDialogProvider>();
        var cut = Render<PersonEventLinks>(p => p.Add(x => x.PersonId, PersonId));
        cut.WaitForAssertion(() => cut.Markup.ShouldContain("Встреча у рынка"));

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Изменить").Click();

        dialogs.WaitForAssertion(() => dialogs.Markup.ShouldContain("Изменить событие и участие"));
        var values = dialogs.FindAll("input, textarea").Select(i => i.GetAttribute("value") ?? i.TextContent).ToList();
        values.ShouldContain("01KG123ABC");
        values.ShouldContain("Toyota");
        values.ShouldContain("г. Ош, ул. Ленина, 1");
        values.ShouldContain("водитель");
        values.ShouldContain("Встреча у рынка");
        dialogs.Markup.ShouldNotContain("_plate");
    }
}
