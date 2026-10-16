using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Profiles;
using ISC.AI.Profile.Investigation.Application.Features.Briefs;
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

// Настройка подмены NSubstitute: ValueTask здесь не исполняется, а задаёт ответ (CA2012 к этому неприменим).
#pragma warning disable CA2012

/// <summary>
/// Страница справки по делу и по фигуранту (ТФ-ОТЧ-01/02, ADR-0053): факты — при открытии, с грифом справки; изложение —
/// только по кнопке, потоком; после итога текст можно править и видны замечания проверки; «Скачать .docx» отправляет на
/// сервер вид, предмет, период и ИСПРАВЛЕННЫЙ текст; отказ сервера — пояснением без фактов.
/// </summary>
public sealed class BriefPageTests : BunitContext, IAsyncLifetime
{
    private readonly IMediator _mediator = Substitute.For<IMediator>();

    private static readonly Brief CaseBrief = new(
        BriefKind.Case, 3, 3, "Справка по делу № 12-2026/045", "«Наблюдение» · период 01.09.2026 — 15.10.2026",
        new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 15), new DateTime(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc), 2, 5,
        [
            new BriefSection("Дело", [new BriefFact("Номер", "12-2026/045")]),
            new BriefSection("Фигуранты", [new BriefFact("Фигурантов", "1")],
                new BriefTable(["Фигурант", "Роль"], [["Иванов Иван", "объект"]], 3)),
            new BriefSection("Связи", []),
        ]);

    public BriefPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton(_mediator);
        var profile = Substitute.For<IProfile>();
        profile.DisplayName.Returns("Следствие");
        Services.AddSingleton(profile);
        SetRendererInfo(new RendererInfo("Server", true));
        _mediator.Send(Arg.Any<GetBriefQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<Brief>>(ResponseDto<Brief>.Ok(CaseBrief)));
    }

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

    Task IAsyncLifetime.DisposeAsync() => base.DisposeAsync().AsTask();

    [Fact(DisplayName = "Справка по делу: факты при открытии — заголовок, гриф справки, разделы, таблица и «не вошло»; модель не вызывается")]
    public void Case_brief_shows_facts()
    {
        var cut = RenderPage(caseId: 3);

        cut.WaitForAssertion(() => cut.FindAll(".brief-section").Count.ShouldBe(3));
        cut.Find("h4").TextContent.ShouldBe("Справка по делу № 12-2026/045");
        cut.Markup.ShouldContain("гриф справки");
        cut.Find(".brief-table td[data-label='Фигурант']").TextContent.ShouldBe("Иванов Иван");
        cut.Markup.ShouldContain("Не вошло в таблицу: 2 из 3.");
        cut.Markup.ShouldContain("Сведений нет.");
        cut.FindAll(".brief-period").ShouldNotBeEmpty();
        _mediator.Received(1).Send(Arg.Is<GetBriefQuery>(q => q.Kind == BriefKind.Case && q.Id == 3 && q.From == null && q.To == null), Arg.Any<CancellationToken>());
        _mediator.DidNotReceiveWithAnyArgs().CreateStream(default(WriteBriefSummaryCommand)!, default);
    }

    [Fact(DisplayName = "Изложение: поток по кнопке; после итога — правимый текст без разметки и замечания проверки; «Скачать» отправляет исправленный текст и период")]
    public void Summary_is_written_edited_and_exported()
    {
        _mediator.CreateStream(Arg.Any<WriteBriefSummaryCommand>(), Arg.Any<CancellationToken>()).Returns(Updates(
            new BriefSummaryUpdate(TextDelta: "**Дело** ведётся"),
            new BriefSummaryUpdate(Final: new BriefSummary("**Дело** ведётся с 01.09.2026; 9 поисков.", 2, ["№ 99"], ["9"]))));
        ExportBriefCommand? exported = null;
        _mediator.Send(Arg.Do<ExportBriefCommand>(c => exported = c), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<BriefFile>>(ResponseDto<BriefFile>.Ok(new BriefFile([1, 2], "справка.docx", ExportBriefCommand.DocxContentType))));
        var cut = RenderPage(caseId: 3);
        cut.WaitForAssertion(() => cut.Find("button.brief-write"));

        cut.Find("button.brief-write").Click();

        cut.WaitForAssertion(() => cut.Find(".brief-summary textarea").GetAttribute("value").ShouldBe("Дело ведётся с 01.09.2026; 9 поисков."));
        cut.Find(".brief-unverified").TextContent.ShouldContain("№ 99");
        cut.Find(".brief-not-in-facts").TextContent.ShouldContain("9");
        _mediator.Received(1).CreateStream(
            Arg.Is<WriteBriefSummaryCommand>(c => c.Kind == BriefKind.Case && c.Id == 3 && c.From == new DateOnly(2026, 9, 1) && c.To == new DateOnly(2026, 10, 15)),
            Arg.Any<CancellationToken>());

        cut.Find(".brief-summary textarea").Input("Дело ведётся. Проверено сотрудником.");
        cut.Find("button.brief-export").Click();

        cut.WaitForAssertion(() => exported.ShouldNotBeNull());
        exported!.Summary.ShouldBe("Дело ведётся. Проверено сотрудником.");
        exported.Kind.ShouldBe(BriefKind.Case);
        exported.From.ShouldBe(new DateOnly(2026, 9, 1));
        exported.SummaryClassification.ShouldBe((short)2, "гриф, выданный изложению сервером, уходит с выгрузкой и может только поднять гриф файла");
        cut.WaitForAssertion(() => JSInterop.Invocations.Select(i => i.Identifier).ShouldContain("downloadFileFromStream"));
    }

    [Fact(DisplayName = "Показ фактов за другой период убирает изложение прежнего периода; выгрузка — за показанный период, без чужого изложения и его грифа")]
    public void New_period_drops_previous_summary()
    {
        _mediator.CreateStream(Arg.Any<WriteBriefSummaryCommand>(), Arg.Any<CancellationToken>()).Returns(Updates(
            new BriefSummaryUpdate(Final: new BriefSummary("Изложение за сентябрь.", 3, [], []))));
        var october = CaseBrief with { From = new DateOnly(2026, 10, 1), Subtitle = "период 01.10.2026 — 15.10.2026" };
        _mediator.Send(Arg.Any<GetBriefQuery>(), Arg.Any<CancellationToken>()).Returns(
            new ValueTask<ResponseDto<Brief>>(ResponseDto<Brief>.Ok(CaseBrief)),
            new ValueTask<ResponseDto<Brief>>(ResponseDto<Brief>.Ok(october)));
        ExportBriefCommand? exported = null;
        _mediator.Send(Arg.Do<ExportBriefCommand>(c => exported = c), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<BriefFile>>(ResponseDto<BriefFile>.Ok(new BriefFile([1], "справка.docx", ExportBriefCommand.DocxContentType))));
        var cut = RenderPage(caseId: 3);
        cut.WaitForAssertion(() => cut.Find("button.brief-write"));
        cut.Find("button.brief-write").Click();
        cut.WaitForAssertion(() => cut.FindAll(".brief-summary").ShouldNotBeEmpty());

        cut.Find("button.brief-period").Click();

        cut.WaitForAssertion(() => _mediator.Received(2).Send(Arg.Any<GetBriefQuery>(), Arg.Any<CancellationToken>()));
        cut.WaitForAssertion(() => cut.FindAll(".brief-summary").ShouldBeEmpty());
        cut.FindAll(".brief-period-pending").ShouldBeEmpty();
        cut.Find("button.brief-export").Click();
        cut.WaitForAssertion(() => exported.ShouldNotBeNull());
        (exported!.From, exported.To).ShouldBe((new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 15)));
        exported.Summary.ShouldBeNullOrEmpty();
        exported.SummaryClassification.ShouldBe((short)0);
    }

    [Fact(DisplayName = "Неудачное «Написать заново» оставляет прежнее изложение вместе с его замечаниями, а не текст без них")]
    public void Failed_rewrite_keeps_previous_summary_and_checks()
    {
        _mediator.CreateStream(Arg.Any<WriteBriefSummaryCommand>(), Arg.Any<CancellationToken>()).Returns(
            Updates(new BriefSummaryUpdate(Final: new BriefSummary("Проведено 9 поисков.", 2, [], ["9"]))),
            Updates(new BriefSummaryUpdate(TextDelta: "Новый"), new BriefSummaryUpdate(Error: BriefMessages.ModelUnavailable)));
        var cut = RenderPage(caseId: 3);
        cut.WaitForAssertion(() => cut.Find("button.brief-write"));
        cut.Find("button.brief-write").Click();
        cut.WaitForAssertion(() => cut.Find(".brief-not-in-facts"));

        cut.Find("button.brief-write").Click();

        cut.WaitForAssertion(() => cut.Markup.ShouldContain(BriefMessages.ModelUnavailable));
        cut.Find(".brief-summary textarea").GetAttribute("value").ShouldBe("Проведено 9 поисков.");
        cut.Find(".brief-not-in-facts").TextContent.ShouldContain("9");
    }

    [Fact(DisplayName = "Переход к другой справке на той же странице останавливает поток изложения прежней: её текст сюда не попадает, кнопки свободны")]
    public void Switching_brief_cancels_running_summary()
    {
        var token = CancellationToken.None;
        _mediator.CreateStream(Arg.Any<WriteBriefSummaryCommand>(), Arg.Any<CancellationToken>())
            .Returns(ci => Hang(token = ci.ArgAt<CancellationToken>(1)));
        var cut = RenderPage(caseId: 3);
        cut.WaitForAssertion(() => cut.Find("button.brief-write"));
        cut.Find("button.brief-write").Click();
        cut.WaitForAssertion(() => cut.Find("button.brief-stop"));

        cut.Render(p =>
        {
            p.Add(x => x.CaseId, (int?)null);
            p.Add(x => x.PersonId, 7);
        });

        cut.WaitForAssertion(() => token.IsCancellationRequested.ShouldBeTrue());
        cut.WaitForAssertion(() => cut.Find("button.brief-write").HasAttribute("disabled").ShouldBeFalse());
        cut.Markup.ShouldNotContain("Изложение остановлено.");
        cut.FindAll(".brief-draft").ShouldBeEmpty();
        cut.FindAll(".brief-summary").ShouldBeEmpty();
    }

    [Fact(DisplayName = "Справка по фигуранту: запрос по фигуранту, без выбора периода; ошибка изложения — пояснением, факты остаются")]
    public void Person_brief_and_summary_error()
    {
        _mediator.CreateStream(Arg.Any<WriteBriefSummaryCommand>(), Arg.Any<CancellationToken>())
            .Returns(Updates(new BriefSummaryUpdate(Error: BriefMessages.ModelUnavailable)));
        var cut = RenderPage(personId: 7);
        cut.WaitForAssertion(() => cut.FindAll(".brief-section").ShouldNotBeEmpty());

        cut.FindAll(".brief-period").ShouldBeEmpty();
        _mediator.Received(1).Send(Arg.Is<GetBriefQuery>(q => q.Kind == BriefKind.Person && q.Id == 7), Arg.Any<CancellationToken>());
        cut.Find("button.brief-write").Click();

        cut.WaitForAssertion(() => cut.Markup.ShouldContain(BriefMessages.ModelUnavailable));
        cut.FindAll(".brief-section").ShouldNotBeEmpty();
        cut.FindAll(".brief-summary").ShouldBeEmpty();
    }

    [Fact(DisplayName = "Отказ сервера (нет права или нет доступа) — пояснение, ни фактов, ни кнопок")]
    public void Refusal_shows_explanation()
    {
        _mediator.Send(Arg.Any<GetBriefQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<Brief>>(ResponseDto<Brief>.BadRequest(BriefMessages.NoRight)));

        var cut = RenderPage(caseId: 3);

        cut.WaitForAssertion(() => cut.Markup.ShouldContain(BriefMessages.NoRight));
        cut.FindAll(".brief-section").ShouldBeEmpty();
        cut.FindAll("button.brief-write").ShouldBeEmpty();
    }

    private IRenderedComponent<BriefPage> RenderPage(int? caseId = null, int? personId = null)
    {
        Render<MudPopoverProvider>();
        return Render<BriefPage>(p =>
        {
            p.Add(x => x.CaseId, caseId);
            p.Add(x => x.PersonId, personId);
        });
    }

    private static async IAsyncEnumerable<BriefSummaryUpdate> Updates(params BriefSummaryUpdate[] updates)
    {
        foreach (var update in updates)
        {
            await Task.Yield();
            yield return update;
        }
    }

    // Поток, который отдаёт кусок черновика и ждёт до отмены (модель «думает»).
    private static async IAsyncEnumerable<BriefSummaryUpdate> Hang([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();
        yield return new BriefSummaryUpdate(TextDelta: "Дело ");
        await Task.Delay(Timeout.Infinite, cancellationToken);
    }
}
