using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Enums;
using ISC.AI.Profile.Investigation.Application.Features.CaseChat;
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
/// Чат по материалам (ADR-0051): вкладка дела и страница меню. Без права — пояснение; строка «что видит чат»; вопрос —
/// поток черновика, итог с источниками (документ, расшифровка на моменте записи), выделенными ссылками и предупреждением
/// о непроверенном; прежний диалог; поиск по смыслу; отказ сервера; на странице — выбор области «все дела» или дело.
/// </summary>
public sealed class CaseChatPanelTests : BunitContext, IAsyncLifetime
{
    private const string ModulePath = "./_content/ISC.AI.Profile.Investigation.UI/js/caseChat.js";
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private bool _popoverRendered;

    private static readonly CaseChatSource Document =
        new(1, ChatSourceKind.Document, 77, 5, "№ 12-3/2026 от 01.10.2026 · Справка · дело № 45", 900, 1, "Изъяты телефон и записная книжка.");

    private static readonly CaseChatSource Record =
        new(2, ChatSourceKind.Transcript, 9, 5, "Расшифровка «звонок.mp3» · носитель № 9 · дело № 45", 901, 1, "[00:01:05] Встречаемся у рынка", 65_000);

    private static readonly ChatCoverage Coverage = new(1, 2, 1, 3, 2, 4, 1);

    public CaseChatPanelTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.SetupModule(ModulePath).Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton(_mediator);
        var profile = Substitute.For<ISC.AI.Abstractions.Profiles.IProfile>();
        profile.DisplayName.Returns("СледствиеAI");
        Services.AddSingleton(profile);
        SetRendererInfo(new RendererInfo("Server", true));
        Overview(new CaseChatOverview([new CaseChatSummary(15, "Что изъяли?", 2, DateTime.UtcNow)], Coverage));
    }

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

    Task IAsyncLifetime.DisposeAsync() => base.DisposeAsync().AsTask();

    [Fact(DisplayName = "Без права «Чат по материалам дел» — только пояснение, ни ленты, ни поиска")]
    public void No_right_shows_explanation()
    {
        _mediator.Send(Arg.Any<ListCaseChatsQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<CaseChatOverview>>(ResponseDto<CaseChatOverview>.BadRequest(CaseChatMessages.NoRight)));

        var cut = RenderPanel(5);

        cut.WaitForAssertion(() => cut.Markup.ShouldContain(CaseChatMessages.NoRight));
        cut.FindAll("textarea").ShouldBeEmpty();
        cut.Markup.ShouldNotContain("Найти по смыслу");
    }

    [Fact(DisplayName = "Строка готовности: что видит чат — документы, расшифровки, сводки, сведения; запрос — по делу вкладки")]
    public void Coverage_line()
    {
        var cut = RenderPanel(5);

        cut.WaitForAssertion(() => cut.Find(".cc-coverage").TextContent.Trim()
            .ShouldBe("Чат видит: документов 1 из 2 · расшифровок 2 из 3 · сводок и справок 4 · сведений дел 1"));
        cut.Markup.ShouldContain("Что изъяли?");
        _mediator.Received(1).Send(Arg.Is<ListCaseChatsQuery>(q => q.CaseId == 5), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Вопрос: черновик по кускам, итог — «[Фрагмент 1]» выделен с подписью, источники ведут на документ и на момент записи, непроверенное — предупреждением")]
    public void Question_streams_and_shows_sources()
    {
        SendCaseChatMessageCommand? sent = null;
        _mediator.CreateStream(Arg.Do<SendCaseChatMessageCommand>(c => sent = c), Arg.Any<CancellationToken>()).Returns(Updates(
            new CaseChatUpdate(ChatId: 15),
            new CaseChatUpdate(TextDelta: "Изъяты "),
            new CaseChatUpdate(ChatId: 15, Final: new CaseChatAnswer(
                "Изъяты телефон и книжка [Фрагмент 1], встреча у рынка [Фрагмент 2], см. № 99/2025.", 2, [Document, Record],
                new CaseChatCheck(["№ 99/2025"], [])))));
        var cut = RenderPanel(5);
        cut.WaitForAssertion(() => cut.Find("textarea"));

        cut.Find("textarea").Input("Что изъяли?");
        cut.Find("button.cc-ask").Click();

        cut.WaitForAssertion(() => cut.Markup.ShouldContain("Источники:"));
        sent.ShouldNotBeNull().ShouldBe(new SendCaseChatMessageCommand(5, null, "Что изъяли?"));
        var mark = cut.FindAll(".cc-mark").First();
        mark.TextContent.ShouldBe("[Фрагмент 1]");
        mark.GetAttribute("title").ShouldBe(Document.Label);
        cut.FindAll(".cc-source a").Select(a => a.GetAttribute("href")).ShouldBe(["/docflow/documents/77", "/media/assets/9?t=65000"]);
        cut.FindAll(".cc-source a")[1].TextContent.ShouldContain("[00:01:05]");
        cut.Find(".cc-check").TextContent.ShouldContain("№ 99/2025");
    }

    [Fact(DisplayName = "Отказ сервера во время вопроса — предупреждение, пустой ответ не остаётся в ленте")]
    public void Server_refusal_is_shown()
    {
        _mediator.CreateStream(Arg.Any<SendCaseChatMessageCommand>(), Arg.Any<CancellationToken>())
            .Returns(Updates(new CaseChatUpdate(Error: CaseChatMessages.NoMaterials)));
        var cut = RenderPanel(5);
        cut.WaitForAssertion(() => cut.Find("textarea"));

        cut.Find("textarea").Input("Вопрос");
        cut.Find("button.cc-ask").Click();

        cut.WaitForAssertion(() => cut.Markup.ShouldContain(CaseChatMessages.NoMaterials));
        cut.FindAll(".cc-question").Count.ShouldBe(1);
        cut.FindAll(".cc-answer").ShouldBeEmpty("пустой пузырь ответа не остаётся");
    }

    [Fact(DisplayName = "Подсказки: в пустом чате — вопросы для области, щелчок задаёт вопрос сразу; в идущем диалоге подсказки остаются")]
    public void Suggestions_ask_immediately()
    {
        SendCaseChatMessageCommand? sent = null;
        _mediator.CreateStream(Arg.Do<SendCaseChatMessageCommand>(c => sent = c), Arg.Any<CancellationToken>()).Returns(Updates(
            new CaseChatUpdate(ChatId: 15),
            new CaseChatUpdate(ChatId: 15, Final: new CaseChatAnswer("Ответ [Фрагмент 1].", 1, [Document], new CaseChatCheck([], [])))));
        var cut = RenderPanel(null);
        cut.WaitForAssertion(() => cut.FindAll(".cc-empty").ShouldNotBeEmpty());
        cut.Find(".cc-empty").TextContent.ShouldContain(ChatSuggestions.AllCases[0]);

        cut.FindAll(".cc-empty .mud-chip").First(c => c.TextContent.Contains(ChatSuggestions.AllCases[1])).Click();

        cut.WaitForAssertion(() => cut.FindAll(".cc-answer").ShouldNotBeEmpty());
        sent.ShouldNotBeNull().ShouldBe(new SendCaseChatMessageCommand(null, null, ChatSuggestions.AllCases[1]));
        cut.Find(".cc-hints").TextContent.ShouldContain(ChatSuggestions.AllCases[0]);

        var inCase = RenderPanel(5);
        inCase.WaitForAssertion(() => inCase.Find(".cc-empty").TextContent.ShouldContain(ChatSuggestions.OneCase[0]));
    }

    [Fact(DisplayName = "Ответ оформлен: заголовок из **…**, пункты списка, жирный внутри строки; ссылки выделены; HTML из ответа — текстом")]
    public void Answer_is_formatted_as_text()
    {
        _mediator.CreateStream(Arg.Any<SendCaseChatMessageCommand>(), Arg.Any<CancellationToken>()).Returns(Updates(
            new CaseChatUpdate(ChatId: 15),
            new CaseChatUpdate(ChatId: 15, Final: new CaseChatAnswer(
                "**Дело № 45:**\n*   Петров — **Toyota Camry** [Фрагмент 1]\n<script>alert(1)</script>", 1, [Document], new CaseChatCheck([], [])))));
        var cut = RenderPanel(5);
        cut.WaitForAssertion(() => cut.Find("textarea"));

        cut.Find("textarea").Input("Машины?");
        cut.Find("button.cc-ask").Click();

        cut.WaitForAssertion(() => cut.FindAll(".cc-answer .cc-h").ShouldNotBeEmpty());
        cut.Find(".cc-answer .cc-h").TextContent.Trim().ShouldBe("Дело № 45:");
        cut.Find(".cc-answer .cc-li b").TextContent.ShouldBe("Toyota Camry");
        cut.Find(".cc-answer .cc-li .cc-mark").TextContent.ShouldBe("[Фрагмент 1]");
        cut.FindAll(".cc-answer script").ShouldBeEmpty();
        cut.Find(".cc-answer").TextContent.ShouldContain("<script>alert(1)</script>");
    }

    [Fact(DisplayName = "Подсказки поиска по смыслу: щелчок ищет сразу")]
    public void Sense_suggestion_searches()
    {
        _mediator.Send(Arg.Any<SearchCaseDocumentsQuery>(), Arg.Any<CancellationToken>()).Returns(new ValueTask<ResponseDto<CaseSenseSearchResult>>(
            ResponseDto<CaseSenseSearchResult>.Ok(new CaseSenseSearchResult([], 4))));
        var cut = RenderPanel(5);
        cut.WaitForAssertion(() => cut.FindAll("button").Single(b => b.TextContent.Contains("Найти по смыслу")));
        cut.FindAll("button").Single(b => b.TextContent.Contains("Найти по смыслу")).Click();

        cut.FindAll(".cc-hints .mud-chip").First(c => c.TextContent.Contains(ChatSuggestions.Sense[1])).Click();

        cut.WaitForAssertion(() => _mediator.Received(1).Send(
            Arg.Is<SearchCaseDocumentsQuery>(q => q.CaseId == 5 && q.Text == ChatSuggestions.Sense[1]), Arg.Any<CancellationToken>()));
    }

    [Fact(DisplayName = "Прежний диалог: сообщения с источниками и пометкой о скрытых выше допуска")]
    public void Opening_chat_shows_history()
    {
        _mediator.Send(Arg.Any<GetCaseChatQuery>(), Arg.Any<CancellationToken>()).Returns(new ValueTask<ResponseDto<CaseChatThread>>(
            ResponseDto<CaseChatThread>.Ok(new CaseChatThread(15, 5, "Что изъяли?", 2,
            [
                new CaseChatMessageRow(1, ConversationMessageRole.User, "Что изъяли?", 2, DateTime.UtcNow, [], null),
                new CaseChatMessageRow(2, ConversationMessageRole.Assistant, "Телефон [Фрагмент 1].", 2, DateTime.UtcNow, [Document], new CaseChatCheck([], [])),
            ], HiddenMessages: 1))));
        var cut = RenderPanel(5);
        cut.WaitForAssertion(() => cut.Find(".cc-chat-open"));

        cut.Find(".cc-chat-open").Click();

        cut.WaitForAssertion(() => cut.Markup.ShouldContain("Скрыто сообщений: 1"));
        cut.Find(".cc-source").TextContent.ShouldContain("12-3/2026");
        cut.FindAll(".cc-check").ShouldBeEmpty();
        _mediator.Received(1).Send(Arg.Is<GetCaseChatQuery>(q => q.ChatId == 15), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Поиск по смыслу: запрос по области, фрагменты с видом материала, близостью и ссылкой на место")]
    public void Sense_search_shows_hits()
    {
        _mediator.Send(Arg.Any<SearchCaseDocumentsQuery>(), Arg.Any<CancellationToken>()).Returns(new ValueTask<ResponseDto<CaseSenseSearchResult>>(
            ResponseDto<CaseSenseSearchResult>.Ok(new CaseSenseSearchResult([new CaseSenseHit(Record, "[00:01:05] Встречаемся у рынка", 0.75)], 4))));
        var cut = RenderPanel(5);
        cut.WaitForAssertion(() => cut.FindAll("button").Single(b => b.TextContent.Contains("Найти по смыслу")));

        cut.FindAll("button").Single(b => b.TextContent.Contains("Найти по смыслу")).Click();
        cut.Find("input").Input("встреча на рынке");
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Найти").Click();

        cut.WaitForAssertion(() => cut.Find(".cc-hit").TextContent.ShouldContain("Встречаемся у рынка"));
        cut.Find(".cc-hit").TextContent.ShouldContain("расшифровка · близость 0,75");
        cut.Find(".cc-hit a").GetAttribute("href").ShouldBe("/media/assets/9?t=65000");
        _mediator.Received(1).Send(Arg.Is<SearchCaseDocumentsQuery>(q => q.CaseId == 5 && q.Text == "встреча на рынке"), Arg.Any<CancellationToken>());
    }

    [Theory(DisplayName = "Страница «Чат по материалам»: по умолчанию — все мои дела; дело из адреса — только доступное, иначе все дела")]
    [InlineData("/chat", null)]
    [InlineData("/chat?case=5", 5)]
    [InlineData("/chat?case=99", null)]
    public void Chat_page_scope(string url, int? expected)
    {
        _mediator.Send(Arg.Any<ListChatScopesQuery>(), Arg.Any<CancellationToken>()).Returns(new ValueTask<ResponseDto<IReadOnlyList<CaseChatScope>>>(
            ResponseDto<IReadOnlyList<CaseChatScope>>.Ok([new CaseChatScope(5, "45", 1, 3, "Наблюдение"), new CaseChatScope(6, "46", 1, 3, "")])));
        EnsurePopover();
        Services.GetRequiredService<NavigationManager>().NavigateTo(url);

        var cut = Render<ChatPage>();

        cut.WaitForAssertion(() => cut.FindComponent<CaseChatPanel>().Instance.CaseId.ShouldBe(expected));
        cut.FindComponent<MudSelect<int>>().Instance.Value.ShouldBe(expected ?? 0);
        _mediator.Received(1).Send(Arg.Is<ListCaseChatsQuery>(q => q.CaseId == expected), Arg.Any<CancellationToken>());
        cut.FindAll("a").Where(a => a.TextContent.Contains("Карточка дела")).Select(a => a.GetAttribute("href"))
            .ShouldBe(expected is { } id ? ["/cases/" + id] : []);
    }

    [Fact(DisplayName = "Страница «Чат по материалам» без права — пояснение, без выбора и чата")]
    public void Chat_page_without_right()
    {
        _mediator.Send(Arg.Any<ListChatScopesQuery>(), Arg.Any<CancellationToken>()).Returns(new ValueTask<ResponseDto<IReadOnlyList<CaseChatScope>>>(
            ResponseDto<IReadOnlyList<CaseChatScope>>.BadRequest(CaseChatMessages.NoRight)));
        EnsurePopover();

        var cut = Render<ChatPage>();

        cut.WaitForAssertion(() => cut.Markup.ShouldContain(CaseChatMessages.NoRight));
        cut.FindComponents<CaseChatPanel>().ShouldBeEmpty();
    }

    // ---------------- обвязка ----------------

    private IRenderedComponent<CaseChatPanel> RenderPanel(int? caseId)
    {
        EnsurePopover();
        return Render<CaseChatPanel>(p => p.Add(x => x.CaseId, caseId));
    }

    // Поставщик всплывающих MudBlazor — один на тест (второй экземпляр конфликтует с первым).
    private void EnsurePopover()
    {
        if (!_popoverRendered)
        {
            Render<MudPopoverProvider>();
            _popoverRendered = true;
        }
    }

    private void Overview(CaseChatOverview overview) =>
        _mediator.Send(Arg.Any<ListCaseChatsQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<CaseChatOverview>>(ResponseDto<CaseChatOverview>.Ok(overview)));

    private static async IAsyncEnumerable<CaseChatUpdate> Updates(params CaseChatUpdate[] updates)
    {
        foreach (var update in updates)
        {
            await Task.Yield();
            yield return update;
        }
    }
}
