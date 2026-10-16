using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.AI;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Conversations;
using ISC.AI.Abstractions.Enums;
using ISC.AI.Abstractions.Grounding;
using ISC.AI.Abstractions.Rag;
using ISC.AI.Abstractions.Retrieval;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Chat.Application;
using ISC.AI.Modules.Chat.Application.Answers;
using ISC.AI.Modules.Chat.Application.Features;
using ISC.AI.Modules.Chat.Domain.Services;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Chat;

/// <summary>
/// Сценарии пакета «Чат» (ADR-0052) поверх порта профиля: вопрос по материалам области — журнал до модели, поиск
/// только в материалах области, источники с подписями профиля, проверка ссылок, ключи уничтожения; свободный режим —
/// без поиска и журнала поиска; отказы до модели; диалог другой или невидимой области — «не найден»; поиск по смыслу;
/// разбор оформления ответа и ссылок на фрагменты.
/// </summary>
public sealed class ChatModuleTests
{
    private const int UserId = 42;

    private static readonly AccessContext Access = new(UserId.ToString(CultureInfo.InvariantCulture), 3, [5]);

    private static readonly ChatArea CaseArea = new(
        "case:3", "№ 45 — Сбыт", "дела № 45 «Сбыт»", 2, 2, 5, "investigation:case:3", ["case:3"], new ChatLink("/cases/3", "Карточка дела"));

    private static readonly ChatArea AllArea = new(null, "Все мои дела (2)", "всех доступных вам дел (2)", 0, 2, null, "investigation", []);

    // ---------------- вопрос по материалам ----------------

    [Fact(DisplayName = "Вопрос по области: новый диалог, журнал — до модели, поиск только в материалах области; источники с подписями профиля, проверка ссылок, ключи уничтожения")]
    public async Task Grounded_question_goes_through_scope_audit_and_check()
    {
        var kit = new Kit();
        var order = new List<string>();
        kit.Audit.When(a => a.WriteAsync(Arg.Any<AuditEntry>(), Arg.Any<CancellationToken>())).Do(_ => order.Add("audit"));
        GroundedRequest? sent = null;
        kit.Generator.GenerateStreamingAsync(Arg.Do<GroundedRequest>(r => { sent = r; order.Add("model"); }), Access, Arg.Any<CancellationToken>())
            .Returns(Stream(
                new GroundedStreamUpdate("Изъяли ", null),
                new GroundedStreamUpdate(null, Final("Изъяли телефон [Фрагмент 1]; встреча [Фрагмент 2]; см. [Фрагмент 5] и № 99/2025."))));

        var updates = await kit.SendAsync(new SendChatMessageCommand("case:3", null, "  Что   изъяли?  "));

        order.ShouldBe(["audit", "model"]);
        updates[0].ConversationId.ShouldBe(15);
        updates[1].TextDelta.ShouldBe("Изъяли ");
        var final = updates[^1].Final.ShouldNotBeNull();

        // Диалог — в области, с её грифом и ключами; заголовок — вопрос без лишних пробелов.
        await kit.Conversations.Received(1).CreateAsync(UserId, "Что изъяли?", "case:3", 2, Arg.Is<IReadOnlyCollection<string>>(k => k.SequenceEqual(new[] { "case:3" })), Arg.Any<CancellationToken>());
        await kit.Conversations.Received(1).AppendMessageAsync(15, UserId, Arg.Is<ConversationMessageDraft>(m =>
            m.Role == ConversationMessageRole.User && m.Content == "Что   изъяли?" && m.Classification == 2), Arg.Any<CancellationToken>());
        await kit.Audit.Received(1).WriteAsync(Arg.Is<AuditEntry>(e =>
            e.Action == AuditAction.Search && e.ObjectRef == "investigation:case:3:chat:15" && e.Classification == 2 && e.DivisionId == 5
            && e.SubjectId == UserId
            && e.PayloadSensitive!.Contains("Что   изъяли?", StringComparison.Ordinal)
            && e.PayloadSensitive.Contains("дела № 45 «Сбыт»", StringComparison.Ordinal)
            && e.PayloadSensitive.Contains("материалов в области: 2", StringComparison.Ordinal)), Arg.Any<CancellationToken>());

        sent.ShouldNotBeNull();
        sent.Filter.ShouldNotBeNull().DocumentIds.ShouldBe([501, 601]);
        sent.Role.ShouldBe(ModelRole.Draft);
        sent.TopK.ShouldBe(7);
        sent.TaskPrompt.ShouldBe("Промпт для дела № 45 «Сбыт»");
        sent.History.ShouldNotBeNull().ShouldBeEmpty();

        // Фрагмент 3 — не из материалов области: в источники не попадает, но номер его подавали (он не «неизвестный»).
        final.Sources.ShouldBe(
        [
            new ChatSource(1, "документ", "Справка № 12-3/2026", "/docflow/documents/77", 900, 1, "Изъяты телефон и книжка по ст. 158.", "case:3"),
            new ChatSource(2, "расшифровка", "Запись · [00:01:05]", "/media/assets/9?t=65000", 901, 1, "[00:01:05] Встречаемся у рынка", "case:4"),
        ]);
        final.Check.ShouldNotBeNull().Unverified.ShouldBe(["№ 99/2025"]);
        final.Check.UnknownFragments.ShouldBe([5]);
        final.Classification.ShouldBe((short)2);

        await kit.Conversations.Received(1).AppendMessageAsync(15, UserId, Arg.Is<ConversationMessageDraft>(m =>
            m.Role == ConversationMessageRole.Assistant
            && m.Classification == 2
            && m.GroundingJson != null
            && m.SourcesJson!.Contains("/docflow/documents/77", StringComparison.Ordinal)
            && m.CheckJson!.Contains("99/2025", StringComparison.Ordinal)
            && m.ScopeKeys!.SequenceEqual(new[] { "case:3", "case:4" })), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Область по умолчанию на весь корпус: без фильтра области, вопрос без грифа, журнал — на объект области с наибольшим грифом")]
    public async Task Whole_corpus_area_has_no_filter()
    {
        var kit = new Kit { Scope = new ChatScope(null, Describe) };
        GroundedRequest? sent = null;
        kit.Generator.GenerateStreamingAsync(Arg.Do<GroundedRequest>(r => sent = r), Access, Arg.Any<CancellationToken>())
            .Returns(Stream(new GroundedStreamUpdate(null, Final("Ответ [Фрагмент 1]."))));

        var updates = await kit.SendAsync(new SendChatMessageCommand(null, null, "Кто ездит на Camry?"));

        updates[^1].Final.ShouldNotBeNull().Classification.ShouldBe((short)1);
        sent.ShouldNotBeNull().Filter.ShouldBeNull();
        await kit.Conversations.Received(1).CreateAsync(UserId, "Кто ездит на Camry?", null, 0, Arg.Is<IReadOnlyCollection<string>>(k => k.Count == 0), Arg.Any<CancellationToken>());
        await kit.Conversations.Received(1).AppendMessageAsync(15, UserId, Arg.Is<ConversationMessageDraft>(m =>
            m.Role == ConversationMessageRole.User && m.Classification == 0), Arg.Any<CancellationToken>());
        await kit.Audit.Received(1).WriteAsync(Arg.Is<AuditEntry>(e =>
            e.ObjectRef == "investigation:chat:15" && e.Classification == 2 && e.DivisionId == null
            && e.PayloadSensitive!.Contains("весь доступный корпус", StringComparison.Ordinal)), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Прежний диалог: история — последние видимые реплики в пределах длины профиля; диалог другой области — «не найден», модель не вызывается")]
    public async Task Existing_conversation_passes_history_and_rejects_other_area()
    {
        var kit = new Kit();
        var messages = Enumerable.Range(1, 5)
            .Select(i => new ConversationMessage(i, i % 2 == 1 ? ConversationMessageRole.User : ConversationMessageRole.Assistant, "реплика " + i, 2, DateTime.UtcNow, null, null))
            .ToList();
        kit.Conversations.GetAsync(15, UserId, (short)3, Arg.Any<CancellationToken>()).Returns(new ConversationThread(15, "case:3", "Т", 2, messages, 0));
        GroundedRequest? sent = null;
        kit.Generator.GenerateStreamingAsync(Arg.Do<GroundedRequest>(r => sent = r), Access, Arg.Any<CancellationToken>())
            .Returns(Stream(new GroundedStreamUpdate(null, Final("Ответ [Фрагмент 1]."))));

        await kit.SendAsync(new SendChatMessageCommand("case:3", 15, "Ещё вопрос"));

        sent.ShouldNotBeNull().History.ShouldNotBeNull().Select(t => (t.Role, t.Text)).ShouldBe(
            [(ConversationMessageRole.Assistant, "репли"), (ConversationMessageRole.User, "репли")]);
        await kit.Conversations.DidNotReceiveWithAnyArgs().CreateAsync(default, default!, default, default, default!, default);

        kit.Generator.ClearReceivedCalls();
        (await kit.SendAsync(new SendChatMessageCommand(null, 15, "Вопрос"))).ShouldHaveSingleItem().Error.ShouldBe(ChatMessages.NotFound);
        kit.Generator.DidNotReceiveWithAnyArgs().GenerateStreamingAsync(default!, default!, default);
    }

    [Fact(DisplayName = "Отказы до модели: пустой вопрос, нет права, свободный режим выключен, область не видна, нет сотрудника, узел ИИ при подготовке, причина профиля, нечего читать")]
    public async Task Refusals_before_model()
    {
        var kit = new Kit();
        (await kit.SendAsync(new SendChatMessageCommand("case:3", null, "   "))).ShouldHaveSingleItem().Error.ShouldBe(ChatMessages.EmptyQuestion);
        (await kit.SendAsync(new SendChatMessageCommand("case:99", null, "Вопрос"))).ShouldHaveSingleItem().Error.ShouldBe(ChatMessages.NotFound);

        kit.Settings = kit.Settings with { AllowFreeMode = false };
        (await kit.SendAsync(new SendChatMessageCommand("case:3", null, "Вопрос", ChatMode.Free))).ShouldHaveSingleItem().Error.ShouldBe(ChatMessages.FreeModeOff);

        kit.Profile.ResolveScopeAsync(Arg.Any<ChatArea>(), Access, true, Arg.Any<CancellationToken>())
            .ThrowsAsync(new ModelUnavailableException(ModelRole.Embeddings, null));
        (await kit.SendAsync(new SendChatMessageCommand("case:3", null, "Вопрос"))).ShouldHaveSingleItem().Error.ShouldBe(ChatMessages.ModelUnavailable);

        kit.Scope = new ChatScope([], Describe, Unavailable: "У вас нет доступных дел — спрашивать не по чему.");
        kit.Profile.ResolveScopeAsync(Arg.Any<ChatArea>(), Access, true, Arg.Any<CancellationToken>()).Returns(_ => kit.Scope);
        (await kit.SendAsync(new SendChatMessageCommand("case:3", null, "Вопрос"))).ShouldHaveSingleItem().Error.ShouldBe("У вас нет доступных дел — спрашивать не по чему.");

        kit.Scope = new ChatScope([], Describe);
        (await kit.SendAsync(new SendChatMessageCommand("case:3", null, "Вопрос"))).ShouldHaveSingleItem().Error.ShouldBe(ChatMessages.NoMaterials);

        kit.AccessProvider.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(new AccessContext("dev", 3, [5]));
        kit.Profile.ResolveAreaAsync("case:3", Arg.Any<AccessContext>(), Arg.Any<CancellationToken>()).Returns(CaseArea);
        (await kit.SendAsync(new SendChatMessageCommand("case:3", null, "Вопрос"))).ShouldHaveSingleItem().Error.ShouldBe(ChatMessages.NoSubject);

        kit.Profile.CanUseAsync(Arg.Any<CancellationToken>()).Returns(false);
        (await kit.SendAsync(new SendChatMessageCommand("case:3", null, "Вопрос"))).ShouldHaveSingleItem().Error.ShouldBe(ChatMessages.NoRight);

        kit.Generator.DidNotReceiveWithAnyArgs().GenerateStreamingAsync(default!, default!, default);
        await kit.Audit.DidNotReceiveWithAnyArgs().WriteAsync(default!, default);
        await kit.Conversations.DidNotReceiveWithAnyArgs().CreateAsync(default, default!, default, default, default!, default);
    }

    [Fact(DisplayName = "Узел ИИ недоступен при ответе: понятная ошибка с номером диалога, вопрос сохранён, ответа нет")]
    public async Task Model_unavailable_is_reported()
    {
        var kit = new Kit();
        kit.Generator.GenerateStreamingAsync(Arg.Any<GroundedRequest>(), Access, Arg.Any<CancellationToken>())
            .Returns(Failing<GroundedStreamUpdate>(new ModelUnavailableException(ModelRole.Draft, null)));

        var updates = await kit.SendAsync(new SendChatMessageCommand("case:3", null, "Вопрос"));

        updates[^1].Error.ShouldNotBeNull().ShouldStartWith(ChatMessages.ModelUnavailable);
        updates[^1].ConversationId.ShouldBe(15);
        await kit.Conversations.Received(1).AppendMessageAsync(15, UserId, Arg.Is<ConversationMessageDraft>(m => m.Role == ConversationMessageRole.User), Arg.Any<CancellationToken>());
        await kit.Conversations.DidNotReceive().AppendMessageAsync(15, UserId, Arg.Is<ConversationMessageDraft>(m => m.Role == ConversationMessageRole.Assistant), Arg.Any<CancellationToken>());
    }

    // ---------------- свободный режим ----------------

    [Fact(DisplayName = "Свободный режим: ассистент ядра с историей, без материалов и журнала поиска; ответ без грифа, без источников и проверки")]
    public async Task Free_mode_streams_without_scope()
    {
        var kit = new Kit();
        kit.Free.GenerateStreamingAsync("Помоги с выводом", Arg.Any<IReadOnlyList<ChatTurn>>(), Access, ModelRole.Draft, Arg.Any<CancellationToken>())
            .Returns(Stream("Вывод: ", "нарушений нет."));

        var updates = await kit.SendAsync(new SendChatMessageCommand(null, null, "Помоги с выводом", ChatMode.Free));

        updates.Select(u => u.TextDelta).OfType<string>().ShouldBe(["Вывод: ", "нарушений нет."]);
        var final = updates[^1].Final.ShouldNotBeNull();
        final.Text.ShouldBe("Вывод: нарушений нет.");
        final.Sources.ShouldBeEmpty();
        final.Check.ShouldBeNull();
        await kit.Profile.DidNotReceiveWithAnyArgs().ResolveScopeAsync(default!, default!, default, default);
        await kit.Audit.DidNotReceiveWithAnyArgs().WriteAsync(default!, default);
        kit.Generator.DidNotReceiveWithAnyArgs().GenerateStreamingAsync(default!, default!, default);
        await kit.Conversations.Received(1).AppendMessageAsync(15, UserId, Arg.Is<ConversationMessageDraft>(m =>
            m.Role == ConversationMessageRole.Assistant && m.Content == "Вывод: нарушений нет." && m.Classification == 0
            && m.SourcesJson == null && m.CheckJson == null), Arg.Any<CancellationToken>());
    }

    // ---------------- поиск по смыслу ----------------

    [Fact(DisplayName = "Поиск по смыслу: журнал до поиска, в материалах области, близость = 1 − расстояние, фрагмент вне области отброшен")]
    public async Task Sense_search_scopes_to_materials()
    {
        var kit = new Kit();
        var order = new List<string>();
        kit.Audit.When(a => a.WriteAsync(Arg.Any<AuditEntry>(), Arg.Any<CancellationToken>())).Do(_ => order.Add("audit"));
        RetrievalFilter? filter = null;
        kit.Retriever.RetrieveAsync("встреча на рынке", Access, 9, Arg.Do<RetrievalFilter?>(f => { filter = f; order.Add("search"); }), Arg.Any<CancellationToken>())
            .Returns([new RetrievedChunk(901, 601, "[00:01:05] Встречаемся у рынка", 1, 5, true, 0.25), new RetrievedChunk(902, 999, "чужой", 1, 5, true, 0.3)]);

        var response = await kit.SearchAsync(new SearchChatMaterialsQuery("case:3", "  встреча на рынке "));

        order.ShouldBe(["audit", "search"]);
        filter.ShouldNotBeNull().DocumentIds.ShouldBe([501, 601]);
        var result = response.Data.ShouldNotBeNull();
        result.Materials.ShouldBe(2);
        var hit = result.Hits.ShouldHaveSingleItem();
        hit.Similarity.ShouldBe(0.75);
        hit.Source.Href.ShouldBe("/media/assets/9?t=65000");
        await kit.Audit.Received(1).WriteAsync(Arg.Is<AuditEntry>(e =>
            e.Action == AuditAction.Search && e.ObjectRef == "investigation:case:3:materials:sense-search" && e.Classification == 2
            && e.PayloadSensitive!.Contains("встреча на рынке", StringComparison.Ordinal)), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Поиск по смыслу: выключен профилем, пустой запрос, нечего читать, узел ИИ недоступен — без поиска")]
    public async Task Sense_search_refusals()
    {
        var kit = new Kit();
        (await kit.SearchAsync(new SearchChatMaterialsQuery("case:3", " "))).StatusMessage.ShouldBe(ChatMessages.EmptyQuestion);

        kit.Scope = new ChatScope([], Describe, Unavailable: "нечего читать");
        var empty = await kit.SearchAsync(new SearchChatMaterialsQuery("case:3", "x"));
        empty.Status.ShouldBeTrue();
        empty.Data.ShouldNotBeNull().Hits.ShouldBeEmpty();

        kit.Profile.ResolveScopeAsync(Arg.Any<ChatArea>(), Access, true, Arg.Any<CancellationToken>())
            .ThrowsAsync(new ModelUnavailableException(ModelRole.Embeddings, null));
        (await kit.SearchAsync(new SearchChatMaterialsQuery("case:3", "x"))).StatusMessage.ShouldBe(ChatMessages.SenseUnavailable);

        kit.Settings = kit.Settings with { AllowSenseSearch = false };
        (await kit.SearchAsync(new SearchChatMaterialsQuery("case:3", "x"))).StatusMessage.ShouldBe(ChatMessages.SenseSearchOff);

        await kit.Retriever.DidNotReceiveWithAnyArgs().RetrieveAsync(default!, default!, default, default, default);
        await kit.Audit.DidNotReceiveWithAnyArgs().WriteAsync(default!, default);
    }

    // ---------------- чат области и диалоги ----------------

    [Fact(DisplayName = "Чат области: свои диалоги этой области, строка готовности без подготовки материалов, подсказки профиля; без права — отказ")]
    public async Task Overview_lists_own_conversations_of_area()
    {
        var kit = new Kit();
        kit.Conversations.ListInAreaAsync(UserId, "case:3", Arg.Any<CancellationToken>())
            .Returns([new ConversationSummary(15, "Что изъяли?", 2, DateTime.UtcNow, "case:3")]);
        var suggestions = new ChatSuggestions { Grounded = ["Кратко: о чём дело?"] };
        kit.Profile.Suggestions(CaseArea).Returns(suggestions);

        var overview = (await new GetChatOverviewQuery.Handler(kit.Profile, kit.Conversations, kit.AccessProvider)
            .Handle(new GetChatOverviewQuery(" case:3 "), CancellationToken.None)).Data.ShouldNotBeNull();

        overview.Area.ShouldBe(CaseArea);
        overview.Conversations.ShouldHaveSingleItem().Id.ShouldBe(15);
        overview.Coverage.ShouldBe("Чат видит: всё");
        overview.CoverageHint.ShouldBe("подсказка");
        overview.Suggestions.ShouldBe(suggestions);
        await kit.Profile.Received(1).ResolveScopeAsync(CaseArea, Access, false, Arg.Any<CancellationToken>());

        kit.Profile.CanUseAsync(Arg.Any<CancellationToken>()).Returns(false);
        (await new GetChatOverviewQuery.Handler(kit.Profile, kit.Conversations, kit.AccessProvider)
            .Handle(new GetChatOverviewQuery("case:3"), CancellationToken.None)).StatusMessage.ShouldBe(ChatMessages.NoRight);
    }

    [Fact(DisplayName = "Диалог: свой и в видимой области — с источниками и проверкой; область стала не видна — «не найден»; чтение — в журнал")]
    public async Task Conversation_is_shown_while_area_is_visible()
    {
        var kit = new Kit();
        var source = new ChatSource(1, "документ", "Справка", "/docflow/documents/77", 900, 1, "текст", "case:3");
        kit.Conversations.GetAsync(15, UserId, (short)3, Arg.Any<CancellationToken>()).Returns(new ConversationThread(15, "case:3", "Т", 2,
        [
            new ConversationMessage(1, ConversationMessageRole.User, "Вопрос", 2, DateTime.UtcNow, null, null),
            new ConversationMessage(2, ConversationMessageRole.Assistant, "Ответ [Фрагмент 1]", 2, DateTime.UtcNow,
                ChatJson.Write([source]), ChatJson.Write(new ChatCheck(["№ 9"], []))),
        ], 1));
        var handler = new GetChatConversationQuery.Handler(kit.Profile, kit.Conversations, kit.AccessProvider);

        var thread = (await handler.Handle(new GetChatConversationQuery(15), CancellationToken.None)).Data.ShouldNotBeNull();

        thread.AreaKey.ShouldBe("case:3");
        thread.HiddenMessages.ShouldBe(1);
        thread.Messages[1].Sources.ShouldBe([source]);
        thread.Messages[1].Check.ShouldNotBeNull().Unverified.ShouldBe(["№ 9"]);
        thread.Messages[0].Check.ShouldBeNull();
        new GetChatConversationQuery(15).AuditSummary.ShouldBe("chat:conversation:15:view");
        new GetChatConversationQuery(15).AuditAction.ShouldBe(AuditAction.View);

        kit.Profile.ResolveAreaAsync("case:3", Access, Arg.Any<CancellationToken>()).Returns((ChatArea?)null);
        (await handler.Handle(new GetChatConversationQuery(15), CancellationToken.None)).StatusMessage.ShouldBe(ChatMessages.NotFound);
    }

    [Fact(DisplayName = "Переименование и удаление: только свой диалог видимой области; заголовок нормализуется; удаление — скрытие ядра")]
    public async Task Rename_and_delete_own_conversation_of_visible_area()
    {
        var kit = new Kit();
        kit.Conversations.FindAsync(15, UserId, Arg.Any<CancellationToken>()).Returns(new ConversationSummary(15, "Т", 2, DateTime.UtcNow, "case:3"));
        kit.Conversations.RenameAsync(15, UserId, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        kit.Conversations.DeleteAsync(15, UserId, Arg.Any<CancellationToken>()).Returns(true);
        var rename = new RenameChatConversationCommand.Handler(kit.Profile, kit.Conversations, kit.AccessProvider);
        var delete = new DeleteChatConversationCommand.Handler(kit.Profile, kit.Conversations, kit.AccessProvider);

        (await rename.Handle(new RenameChatConversationCommand(15, "  Изъятие   телефона "), CancellationToken.None)).Status.ShouldBeTrue();
        await kit.Conversations.Received(1).RenameAsync(15, UserId, "Изъятие телефона", Arg.Any<CancellationToken>());
        (await delete.Handle(new DeleteChatConversationCommand(15), CancellationToken.None)).Status.ShouldBeTrue();
        new RenameChatConversationCommand(15, "x").AuditSummary.ShouldBe("chat:conversation:15:rename");
        new DeleteChatConversationCommand(15).AuditSummary.ShouldBe("chat:conversation:15:delete");

        // Чужой диалог и диалог невидимой области — «не найден», хранилище не трогается.
        kit.Conversations.ClearReceivedCalls();
        (await rename.Handle(new RenameChatConversationCommand(16, "x"), CancellationToken.None)).StatusMessage.ShouldBe(ChatMessages.NotFound);
        kit.Profile.ResolveAreaAsync("case:3", Access, Arg.Any<CancellationToken>()).Returns((ChatArea?)null);
        (await delete.Handle(new DeleteChatConversationCommand(15), CancellationToken.None)).StatusMessage.ShouldBe(ChatMessages.NotFound);
        await kit.Conversations.DidNotReceiveWithAnyArgs().RenameAsync(default, default, default!, default);
        await kit.Conversations.DidNotReceiveWithAnyArgs().DeleteAsync(default, default, default);
    }

    [Fact(DisplayName = "Хранение источников: запись и чтение без потерь; пустое, старое и испорченное — без падения")]
    public void Json_storage_is_tolerant()
    {
        var source = new ChatSource(1, "документ", "Справка", null, 900, 1, "текст");
        ChatJson.ReadSources(ChatJson.Write([source])).ShouldBe([source]);
        ChatJson.Write(Array.Empty<ChatSource>()).ShouldBeNull();
        ChatJson.ReadCheck(ChatJson.Write(new ChatCheck(["№ 1"], [4]))).ShouldBe(new ChatCheck(["№ 1"], [4]), new ChatCheckComparer());
        ChatJson.ReadSources(null).ShouldBeEmpty();
        ChatJson.ReadSources("{\"citations\":[]}").ShouldBeEmpty();
        ChatJson.ReadSources("не json").ShouldBeEmpty();
        ChatJson.ReadCheck("не json").ShouldBeNull();
        var legacy = ChatJson.ReadCheck("{\"citations\":[],\"allConfirmed\":true}").ShouldNotBeNull();
        legacy.AllConfirmed.ShouldBeTrue();
    }

    // ---------------- оформление ответа ----------------

    [Fact(DisplayName = "«[Фрагмент N]»: номера по порядку без повторов; вне поданных — неизвестны; разбиение сохраняет текст как написан")]
    public void Fragment_references_are_found_checked_and_split()
    {
        const string answer = "Изъятие [Фрагмент 2], протокол во фрагменте 1, и ещё [фрагмент №7]; снова [Фрагмент 2].";

        FragmentReferences.Find(answer).ShouldBe([2, 1, 7]);
        FragmentReferences.Unknown(answer, supplied: 3).ShouldBe([7]);
        var parts = FragmentReferences.Split(answer);
        string.Concat(parts.Select(p => p.Text)).ShouldBe(answer);
        parts.Where(p => p.Fragment is not null).Select(p => (p.Text, p.Fragment)).ShouldBe(
        [
            ("[Фрагмент 2]", (int?)2), ("фрагменте 1", 1), ("[фрагмент №7]", 7), ("[Фрагмент 2]", 2),
        ]);
    }

    [Fact(DisplayName = "Оформление ответа: заголовок «**…**» и «#», пункты «* » и «- », жирный внутри строки и поверх ссылки; непарная «**» — текстом")]
    public void Answer_markup_is_parsed()
    {
        var blocks = AnswerMarkup.Parse(
            "**Дело № Б-2/26 «Сбыт»:**\n\n*   Петров — **Toyota Camry** [Фрагмент 1].\n- **Иванов [Фрагмент 2]**\n### Итог\nВсего 2 ** фигуранта");

        blocks.Select(b => b.Kind).ShouldBe(
            [AnswerBlockKind.Heading, AnswerBlockKind.Bullet, AnswerBlockKind.Bullet, AnswerBlockKind.Heading, AnswerBlockKind.Paragraph]);
        blocks[0].Spans.ShouldBe([new AnswerSpan("Дело № Б-2/26 «Сбыт»:", true, null)]);
        blocks[1].Spans.ShouldBe(
        [
            new AnswerSpan("Петров — ", false, null), new AnswerSpan("Toyota Camry", true, null), new AnswerSpan(" ", false, null),
            new AnswerSpan("[Фрагмент 1]", false, 1), new AnswerSpan(".", false, null),
        ]);
        blocks[2].Spans.ShouldBe([new AnswerSpan("Иванов ", true, null), new AnswerSpan("[Фрагмент 2]", true, 2)]);
        blocks[3].Spans.ShouldBe([new AnswerSpan("Итог", true, null)]);
        string.Concat(blocks[4].Spans.Select(s => s.Text)).ShouldBe("Всего 2 ** фигуранта");
        blocks[4].Spans.ShouldAllBe(s => !s.Bold);
        AnswerMarkup.Parse("  \n ").ShouldBeEmpty();
    }

    // ---------------- обвязка ----------------

    private static readonly List<RetrievedChunk> Fragments =
    [
        new(900, 501, "Изъяты телефон и книжка по ст. 158.", 1, 5, true, 0.2),
        new(901, 601, "[00:01:05] Встречаемся у рынка", 1, 5, true, 0.3),
        new(902, 999, "Фрагмент вне области", 1, 5, true, 0.4),
    ];

    // Подписи «профиля»: документ дела 3 и запись дела 4; остальное — не из области.
    private static ChatSourceRef? Describe(RetrievedChunk fragment) => fragment.DocumentId switch
    {
        501 => new ChatSourceRef("документ", "Справка № 12-3/2026", "/docflow/documents/77", "case:3"),
        601 => new ChatSourceRef("расшифровка", "Запись · [00:01:05]", "/media/assets/9?t=65000", "case:4"),
        _ => null,
    };

    private static GroundedResponse Final(string answer) => new(
        answer,
        new GroundingResult([new CitationCheck("ст. 158", CitationStatus.Confirmed), new CitationCheck("№ 99/2025", CitationStatus.Unverified)], false),
        Fragments,
        1);

    private static async IAsyncEnumerable<T> Stream<T>(params T[] updates)
    {
        foreach (var update in updates)
        {
            await Task.Yield();
            yield return update;
        }
    }

#pragma warning disable CS1998 // асинхронный итератор, который сразу бросает: так ведёт себя генератор при недоступной модели
    private static async IAsyncEnumerable<T> Failing<T>(Exception exception, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        throw exception;
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }
#pragma warning restore CS1998

    // Проверка — запись со списками: сравниваем по содержимому, а не по ссылкам на списки.
    private sealed class ChatCheckComparer : IEqualityComparer<ChatCheck>
    {
        public bool Equals(ChatCheck? x, ChatCheck? y) =>
            x is not null && y is not null && x.Unverified.SequenceEqual(y.Unverified) && x.UnknownFragments.SequenceEqual(y.UnknownFragments);

        public int GetHashCode(ChatCheck obj) => obj.Unverified.Count;
    }

    private sealed class Kit
    {
        public Kit()
        {
            AccessProvider.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(Access);
            Profile.Settings.Returns(_ => Settings);
            Profile.CanUseAsync(Arg.Any<CancellationToken>()).Returns(true);
            Profile.ResolveAreaAsync("case:3", Access, Arg.Any<CancellationToken>()).Returns(CaseArea);
            Profile.ResolveAreaAsync(null, Access, Arg.Any<CancellationToken>()).Returns(AllArea);
            Profile.ResolveScopeAsync(Arg.Any<ChatArea>(), Access, Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(_ => Scope);
            Profile.TaskPrompt(Arg.Any<ChatArea>()).Returns(ci => "Промпт для " + ci.Arg<ChatArea>().Description);
            Conversations.CreateAsync(UserId, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<short>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
                .Returns(15);
            Conversations.AppendMessageAsync(Arg.Any<int>(), UserId, Arg.Any<ConversationMessageDraft>(), Arg.Any<CancellationToken>()).Returns(true);
        }

        public ChatSettings Settings { get; set; } = new()
        {
            AllowFreeMode = true, AllowSenseSearch = true, TopK = 7, HistoryTurns = 2, HistoryTurnLength = 5, SenseSearchTopK = 9,
        };

        public ChatScope Scope { get; set; } = new([501, 601], Describe, "Чат видит: всё", "подсказка");

        public IChatProfile Profile { get; } = Substitute.For<IChatProfile>();

        public IConversationStore Conversations { get; } = Substitute.For<IConversationStore>();

        public IGroundedGenerator Generator { get; } = Substitute.For<IGroundedGenerator>();

        public IConversationalGenerator Free { get; } = Substitute.For<IConversationalGenerator>();

        public IRetriever Retriever { get; } = Substitute.For<IRetriever>();

        public IAuditWriter Audit { get; } = Substitute.For<IAuditWriter>();

        public IAccessContextProvider AccessProvider { get; } = Substitute.For<IAccessContextProvider>();

        public async Task<List<ChatUpdate>> SendAsync(SendChatMessageCommand command)
        {
            var handler = new SendChatMessageCommand.Handler(Profile, Conversations, Generator, Free, Audit, AccessProvider);
            var updates = new List<ChatUpdate>();
            await foreach (var update in handler.Handle(command, CancellationToken.None))
            {
                updates.Add(update);
            }

            return updates;
        }

        public async Task<ResponseDto<ChatSenseResult>> SearchAsync(SearchChatMaterialsQuery query) =>
            await new SearchChatMaterialsQuery.Handler(Profile, Retriever, Audit, AccessProvider).Handle(query, CancellationToken.None);
    }
}
