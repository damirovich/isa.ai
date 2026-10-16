using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Conversations;
using ISC.AI.Abstractions.Enums;
using ISC.AI.Abstractions.Modules;
using ISC.AI.Modules.Chat;
using ISC.AI.Modules.Chat.Application;
using ISC.AI.Modules.Chat.Application.Features;
using ISC.AI.Modules.Chat.Domain.Services;
using ISC.AI.Modules.Chat.UI;
using Mediator;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Chat.UI;

/// <summary>
/// Экран пакета «Чат» (ADR-0052): компонент ChatPanel (страница /chat и вкладка дела) и страница с выбором области.
/// Без права — пояснение; строка «что видит чат»; вопрос — поток черновика, итог с источниками, выделенными ссылками и
/// предупреждением о непроверенном; подсказки; прежний диалог; поиск по смыслу; режимы — какие разрешил профиль
/// (у «Следствия» — «Спросить» и «Найти по смыслу», у «ИнспектораAI» — «Свободный чат» по умолчанию и «По документам»).
/// </summary>
public sealed class ChatPanelTests : BunitContext, IAsyncLifetime
{
    private const string ModulePath = "./_content/ISC.AI.Modules.Chat.UI/js/chat.js";
    private const string CaseKey = "case:5";
    private readonly IMediator _mediator = Substitute.For<IMediator>();
    private readonly ISC.AI.Abstractions.Profiles.IProfile _profile = Substitute.For<ISC.AI.Abstractions.Profiles.IProfile>();
    private bool _popoverRendered;

    // Настройки профиля — до первого рендера (контейнер bUnit после первого обращения не принимает регистраций).
    private ChatSettings _settings = InvestigationSettings;

    private static readonly ChatSettings InvestigationSettings = new()
    {
        Subtitle = "Вопросы к материалам дел",
        AllowSenseSearch = true,
        GroundedModeLabel = "Спросить",
        QuestionPlaceholder = "Ваш вопрос по материалам…",
        EmptyHint = "Документы, расшифровки записей, сводки и справки.",
    };

    private static readonly ChatSettings InspectorSettings = new()
    {
        AllowFreeMode = true,
        StartInFreeMode = true,
        GroundedModeLabel = "По документам",
    };

    private static readonly ChatArea CaseArea = new(
        CaseKey, "№ 45 — Сбыт", "дела № 45 «Сбыт»", 1, 1, 3, "investigation:case:5", [CaseKey], new ChatLink("/cases/5", "Карточка дела"));

    private static readonly ChatArea AllArea = new(null, "Все мои дела (2)", "всех доступных вам дел (2)", 0, 1, null, "investigation", []);

    private static readonly ChatSuggestions Suggestions = new()
    {
        Grounded = ["Кратко: о чём это дело?", "Кто фигуранты и что о них известно?"],
        Free = ["Составь план проверки подразделения"],
        Sense = ["встреча на рынке", "передача денег"],
    };

    private static readonly ChatSource Document =
        new(1, "документ", "№ 12-3/2026 от 01.10.2026 · Справка · дело № 45", "/docflow/documents/77", 900, 1, "Изъяты телефон и записная книжка.", CaseKey);

    private static readonly ChatSource Record =
        new(2, "расшифровка", "Расшифровка «звонок.mp3» · носитель № 9 · дело № 45 · [00:01:05]", "/media/assets/9?t=65000", 901, 1,
            "[00:01:05] Встречаемся у рынка", CaseKey);

    public ChatPanelTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.SetupModule(ModulePath).Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddSingleton(_mediator);
        _profile.DisplayName.Returns("СледствиеAI");
        _profile.Modules.Returns([ChatModule.Page("Чат по материалам", "Дела", Icons.Material.Filled.QuestionAnswer)]);
        Services.AddSingleton(_profile);
        Services.AddSingleton<ChatSettings>(_ => _settings);
        SetRendererInfo(new RendererInfo("Server", true));
        Overview(CaseArea);
        Overview(AllArea);
    }

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;

    Task IAsyncLifetime.DisposeAsync() => base.DisposeAsync().AsTask();

    [Fact(DisplayName = "Без права на чат — только пояснение, ни ленты, ни поиска")]
    public void No_right_shows_explanation()
    {
        _mediator.Send(Arg.Any<GetChatOverviewQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<ChatOverview>>(ResponseDto<ChatOverview>.BadRequest(ChatMessages.NoRight)));

        var cut = RenderPanel(CaseKey);

        cut.WaitForAssertion(() => cut.Markup.ShouldContain(ChatMessages.NoRight));
        cut.FindAll("textarea").ShouldBeEmpty();
        cut.Markup.ShouldNotContain("Найти по смыслу");
    }

    [Fact(DisplayName = "Строка готовности и свои диалоги области; запрос — по области вкладки")]
    public void Coverage_line_and_conversations()
    {
        var cut = RenderPanel(CaseKey);

        cut.WaitForAssertion(() => cut.Find(".cc-coverage").TextContent.Trim().ShouldBe("Чат видит: документов 1 из 2"));
        cut.Find(".cc-coverage").GetAttribute("title").ShouldBe("обновляется при вопросе");
        cut.Markup.ShouldContain("Что изъяли?");
        _mediator.Received(1).Send(Arg.Is<GetChatOverviewQuery>(q => q.AreaKey == CaseKey), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Вопрос: черновик по кускам, итог — «[Фрагмент 1]» выделен с подписью, источники ведут на документ и на момент записи, непроверенное — предупреждением")]
    public void Question_streams_and_shows_sources()
    {
        SendChatMessageCommand? sent = null;
        _mediator.CreateStream(Arg.Do<SendChatMessageCommand>(c => sent = c), Arg.Any<CancellationToken>()).Returns(Updates(
            new ChatUpdate(ConversationId: 15),
            new ChatUpdate(TextDelta: "Изъяты "),
            new ChatUpdate(ConversationId: 15, Final: new ChatAnswer(
                "Изъяты телефон и книжка [Фрагмент 1], встреча у рынка [Фрагмент 2], см. № 99/2025.", 2, [Document, Record],
                new ChatCheck(["№ 99/2025"], [])))));
        var cut = RenderPanel(CaseKey);
        cut.WaitForAssertion(() => cut.Find("textarea"));

        cut.Find("textarea").Input("Что изъяли?");
        cut.Find("button.cc-ask").Click();

        cut.WaitForAssertion(() => cut.Markup.ShouldContain("Источники:"));
        sent.ShouldNotBeNull().ShouldBe(new SendChatMessageCommand(CaseKey, null, "Что изъяли?", ChatMode.Grounded));
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
        _mediator.CreateStream(Arg.Any<SendChatMessageCommand>(), Arg.Any<CancellationToken>())
            .Returns(Updates(new ChatUpdate(Error: ChatMessages.NoMaterials)));
        var cut = RenderPanel(CaseKey);
        cut.WaitForAssertion(() => cut.Find("textarea"));

        cut.Find("textarea").Input("Вопрос");
        cut.Find("button.cc-ask").Click();

        cut.WaitForAssertion(() => cut.Markup.ShouldContain(ChatMessages.NoMaterials));
        cut.FindAll(".cc-question").Count.ShouldBe(1);
        cut.FindAll(".cc-answer").ShouldBeEmpty("пустой пузырь ответа не остаётся");
    }

    [Fact(DisplayName = "Сбой связи во время вопроса — понятный текст без внутренностей, пустой ответ не остаётся")]
    public void Failure_is_shown_without_details()
    {
        _mediator.CreateStream(Arg.Any<SendChatMessageCommand>(), Arg.Any<CancellationToken>())
            .Returns(Failing(new InvalidOperationException("строка подключения Host=secret")));
        var cut = RenderPanel(CaseKey);
        cut.WaitForAssertion(() => cut.Find("textarea"));

        cut.Find("textarea").Input("Вопрос");
        cut.Find("button.cc-ask").Click();

        cut.WaitForAssertion(() => cut.Markup.ShouldContain("Не удалось получить ответ"));
        cut.Markup.ShouldNotContain("secret");
        cut.FindAll(".cc-answer").ShouldBeEmpty();
    }

    [Fact(DisplayName = "Подсказки: в пустом чате — вопросы области, щелчок задаёт вопрос сразу; в идущем диалоге подсказки остаются")]
    public void Suggestions_ask_immediately()
    {
        SendChatMessageCommand? sent = null;
        _mediator.CreateStream(Arg.Do<SendChatMessageCommand>(c => sent = c), Arg.Any<CancellationToken>()).Returns(Updates(
            new ChatUpdate(ConversationId: 15),
            new ChatUpdate(ConversationId: 15, Final: new ChatAnswer("Ответ [Фрагмент 1].", 1, [Document], new ChatCheck([], [])))));
        var cut = RenderPanel(null);
        cut.WaitForAssertion(() => cut.FindAll(".cc-empty").ShouldNotBeEmpty());
        cut.Find(".cc-empty").TextContent.ShouldContain("Задайте вопрос по материалам всех доступных вам дел (2)");
        cut.Find(".cc-empty").TextContent.ShouldContain(InvestigationSettings.EmptyHint!);
        cut.Find(".cc-empty").TextContent.ShouldContain(Suggestions.Grounded[0]);

        cut.FindAll(".cc-empty .mud-chip").First(c => c.TextContent.Contains(Suggestions.Grounded[1])).Click();

        cut.WaitForAssertion(() => cut.FindAll(".cc-answer").ShouldNotBeEmpty());
        sent.ShouldNotBeNull().ShouldBe(new SendChatMessageCommand(null, null, Suggestions.Grounded[1], ChatMode.Grounded));
        cut.Find(".cc-hints").TextContent.ShouldContain(Suggestions.Grounded[0]);
    }

    [Fact(DisplayName = "Ответ оформлен: заголовок из **…**, пункты списка, жирный внутри строки; ссылки выделены; HTML из ответа — текстом")]
    public void Answer_is_formatted_as_text()
    {
        _mediator.CreateStream(Arg.Any<SendChatMessageCommand>(), Arg.Any<CancellationToken>()).Returns(Updates(
            new ChatUpdate(ConversationId: 15),
            new ChatUpdate(ConversationId: 15, Final: new ChatAnswer(
                "**Дело № 45:**\n*   Петров — **Toyota Camry** [Фрагмент 1]\n<script>alert(1)</script>", 1, [Document], new ChatCheck([], [])))));
        var cut = RenderPanel(CaseKey);
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

    [Fact(DisplayName = "Прежний диалог: сообщения с источниками и пометкой о скрытых выше допуска")]
    public void Opening_conversation_shows_history()
    {
        _mediator.Send(Arg.Any<GetChatConversationQuery>(), Arg.Any<CancellationToken>()).Returns(new ValueTask<ResponseDto<ChatThreadView>>(
            ResponseDto<ChatThreadView>.Ok(new ChatThreadView(15, CaseKey, "Что изъяли?", 2,
            [
                new ChatMessageView(1, ConversationMessageRole.User, "Что изъяли?", 2, DateTime.UtcNow, [], null),
                new ChatMessageView(2, ConversationMessageRole.Assistant, "Телефон [Фрагмент 1].", 2, DateTime.UtcNow, [Document], new ChatCheck([], [])),
            ], HiddenMessages: 1))));
        var cut = RenderPanel(CaseKey);
        cut.WaitForAssertion(() => cut.Find(".cc-chat-open"));

        cut.Find(".cc-chat-open").Click();

        cut.WaitForAssertion(() => cut.Markup.ShouldContain("Скрыто сообщений: 1"));
        cut.Find(".cc-source").TextContent.ShouldContain("12-3/2026");
        cut.Find(".cc-mark").GetAttribute("title").ShouldBe(Document.Label);
        cut.FindAll(".cc-check").ShouldBeEmpty();
        _mediator.Received(1).Send(Arg.Is<GetChatConversationQuery>(q => q.ConversationId == 15), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Поиск по смыслу: подсказка ищет сразу; фрагменты с видом материала, близостью, грифом и ссылкой на место")]
    public void Sense_search_shows_hits()
    {
        _mediator.Send(Arg.Any<SearchChatMaterialsQuery>(), Arg.Any<CancellationToken>()).Returns(new ValueTask<ResponseDto<ChatSenseResult>>(
            ResponseDto<ChatSenseResult>.Ok(new ChatSenseResult([new ChatSenseHit(Record, "[00:01:05] Встречаемся у рынка", 0.75)], 4))));
        var cut = RenderPanel(CaseKey);
        cut.WaitForAssertion(() => cut.FindAll("button").Single(b => b.TextContent.Contains("Найти по смыслу")));
        cut.FindAll("button").Single(b => b.TextContent.Contains("Найти по смыслу")).Click();

        cut.FindAll(".cc-hints .mud-chip").First(c => c.TextContent.Contains(Suggestions.Sense[1])).Click();
        cut.WaitForAssertion(() => _mediator.Received(1).Send(
            Arg.Is<SearchChatMaterialsQuery>(q => q.AreaKey == CaseKey && q.Text == Suggestions.Sense[1]), Arg.Any<CancellationToken>()));

        cut.Find("input").Input("встреча на рынке");
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Найти").Click();

        cut.WaitForAssertion(() => cut.Find(".cc-hit").TextContent.ShouldContain("Встречаемся у рынка"));
        cut.Find(".cc-hit").TextContent.ShouldContain("расшифровка · близость 0,75");
        cut.Find(".cc-hit a").GetAttribute("href").ShouldBe("/media/assets/9?t=65000");
        _mediator.Received(1).Send(Arg.Is<SearchChatMaterialsQuery>(q => q.AreaKey == CaseKey && q.Text == "встреча на рынке"), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Режимы «Следствия»: «Спросить» и «Найти по смыслу», свободного чата нет")]
    public void Investigation_modes()
    {
        var cut = RenderPanel(CaseKey);
        cut.WaitForAssertion(() => cut.Find(".mud-toggle-group"));

        var modes = cut.FindAll(".mud-toggle-item").Select(b => b.TextContent.Trim()).ToList();
        modes.ShouldBe(["Спросить", "Найти по смыслу"]);
        cut.Find("textarea").GetAttribute("placeholder").ShouldBe("Ваш вопрос по материалам…");
    }

    [Fact(DisplayName = "Режимы «ИнспектораAI»: начинает со «Свободного чата»; вопрос уходит в свободном режиме, ссылки в свободном ответе не выделяются; «По документам» — режим по материалам")]
    public void Inspector_free_mode_by_default()
    {
        _settings = InspectorSettings;
        var sent = new List<SendChatMessageCommand>();
        _mediator.CreateStream(Arg.Do<SendChatMessageCommand>(sent.Add), Arg.Any<CancellationToken>()).Returns(
            _ => Updates(new ChatUpdate(ConversationId: 15), new ChatUpdate(ConversationId: 15, Final: new ChatAnswer("Набросок [Фрагмент 1].", 0, [], null))));
        var cut = RenderPanel(null);
        cut.WaitForAssertion(() => cut.Find(".mud-toggle-group"));

        cut.FindAll(".mud-toggle-item").Select(b => b.TextContent.Trim()).ShouldBe(["Свободный чат", "По документам"]);
        cut.Find(".cc-empty").TextContent.ShouldContain("Свободный чат: пояснения, формулировки, черновики");
        cut.Find(".cc-empty").TextContent.ShouldContain(Suggestions.Free[0]);
        cut.Markup.ShouldNotContain("Найти по смыслу");

        cut.Find("textarea").Input("Помоги с выводом");
        cut.Find("button.cc-ask").Click();
        cut.WaitForAssertion(() => cut.FindAll(".cc-answer").ShouldNotBeEmpty());

        sent.ShouldHaveSingleItem().Mode.ShouldBe(ChatMode.Free);
        cut.FindAll(".cc-answer .cc-mark").ShouldBeEmpty("в свободном ответе ссылки на фрагменты не сверяются и не выделяются");

        cut.FindAll(".mud-toggle-item").Single(b => b.TextContent.Trim() == "По документам").Click();
        cut.Find("textarea").Input("Что требуется по сроку?");
        cut.Find("button.cc-ask").Click();
        cut.WaitForAssertion(() => sent.Count.ShouldBe(2));
        sent[1].Mode.ShouldBe(ChatMode.Grounded);
        sent[1].ConversationId.ShouldBe(15);
    }

    [Fact(DisplayName = "Только режим по материалам — переключателя нет")]
    public void Single_mode_has_no_toggle()
    {
        _settings = new ChatSettings();
        var cut = RenderPanel(CaseKey);
        cut.WaitForAssertion(() => cut.Find("textarea"));

        cut.FindAll(".mud-toggle-group").ShouldBeEmpty();
        cut.Find(".cc-mode-hint").TextContent.ShouldContain("только по найденным фрагментам");
    }

    [Theory(DisplayName = "Страница чата: по умолчанию — область профиля по умолчанию; область из адреса — только доступная; заголовок — из реестра профиля")]
    [InlineData("/chat", null)]
    [InlineData("/chat?area=case%3A5", CaseKey)]
    [InlineData("/chat?area=case%3A99", null)]
    public void Chat_page_area(string url, string? expected)
    {
        Areas([AllArea, CaseArea]);
        EnsurePopover();
        Services.GetRequiredService<NavigationManager>().NavigateTo(url);

        var cut = Render<ChatPage>();

        cut.WaitForAssertion(() => cut.FindComponent<ChatPanel>().Instance.AreaKey.ShouldBe(expected));
        cut.FindComponent<MudSelect<string>>().Instance.Value.ShouldBe(expected ?? string.Empty);
        cut.Find("h4").TextContent.ShouldBe("Чат по материалам");
        cut.Markup.ShouldContain(InvestigationSettings.Subtitle!);
        _mediator.Received(1).Send(Arg.Is<GetChatOverviewQuery>(q => q.AreaKey == expected), Arg.Any<CancellationToken>());
        cut.FindAll("a").Where(a => a.TextContent.Contains("Карточка дела")).Select(a => a.GetAttribute("href"))
            .ShouldBe(expected is null ? [] : ["/cases/5"]);
    }

    [Fact(DisplayName = "Страница чата с одной областью (как у «ИнспектораAI») — без выбора «Где искать»")]
    public void Chat_page_single_area_has_no_selector()
    {
        Areas([AllArea]);
        EnsurePopover();

        var cut = Render<ChatPage>();

        cut.WaitForAssertion(() => cut.FindComponent<ChatPanel>().Instance.AreaKey.ShouldBeNull());
        cut.FindComponents<MudSelect<string>>().ShouldBeEmpty();
    }

    [Fact(DisplayName = "Страница чата без права — пояснение, без выбора и чата")]
    public void Chat_page_without_right()
    {
        _mediator.Send(Arg.Any<ListChatAreasQuery>(), Arg.Any<CancellationToken>()).Returns(new ValueTask<ResponseDto<IReadOnlyList<ChatArea>>>(
            ResponseDto<IReadOnlyList<ChatArea>>.BadRequest(ChatMessages.NoRight)));
        EnsurePopover();

        var cut = Render<ChatPage>();

        cut.WaitForAssertion(() => cut.Markup.ShouldContain(ChatMessages.NoRight));
        cut.FindComponents<ChatPanel>().ShouldBeEmpty();
    }

    // ---------------- обвязка ----------------

    private IRenderedComponent<ChatPanel> RenderPanel(string? areaKey)
    {
        EnsurePopover();
        return Render<ChatPanel>(p => p.Add(x => x.AreaKey, areaKey));
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

    private void Overview(ChatArea area) =>
        _mediator.Send(Arg.Is<GetChatOverviewQuery>(q => q.AreaKey == area.Key), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ResponseDto<ChatOverview>>(ResponseDto<ChatOverview>.Ok(new ChatOverview(
                area,
                [new ConversationSummary(15, "Что изъяли?", 2, DateTime.UtcNow, area.Key)],
                "Чат видит: документов 1 из 2",
                "обновляется при вопросе",
                Suggestions))));

    private void Areas(IReadOnlyList<ChatArea> areas) =>
        _mediator.Send(Arg.Any<ListChatAreasQuery>(), Arg.Any<CancellationToken>()).Returns(new ValueTask<ResponseDto<IReadOnlyList<ChatArea>>>(
            ResponseDto<IReadOnlyList<ChatArea>>.Ok(areas)));

    private static async IAsyncEnumerable<ChatUpdate> Updates(params ChatUpdate[] updates)
    {
        foreach (var update in updates)
        {
            await Task.Yield();
            yield return update;
        }
    }

#pragma warning disable CS1998 // асинхронный итератор, который сразу бросает: так ведёт себя поток при обрыве связи
    private static async IAsyncEnumerable<ChatUpdate> Failing(Exception exception)
    {
        throw exception;
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }
#pragma warning restore CS1998
}
