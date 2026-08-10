using ISC.AI.AI.Chat;
using ISC.AI.Abstractions.Conversations;
using ISC.AI.Abstractions.Enums;
using ISC.AI.Abstractions.Grounding;
using ISC.AI.Abstractions.Rag;
using ISC.AI.Abstractions.Retrieval;
using ISC.AI.Abstractions.Security;
using NSubstitute;
using Shouldly;

namespace ISC.AI.UnitTests.Chat;

/// <summary>
/// Грунтованный многоходовый ассистент (ChatService): новый диалог создаётся, обе реплики сохраняются,
/// история передаётся в генератор, чужой диалог — отказ ДО модели, без числового субъекта — отказ.
/// Режимные инварианты (грунтовка/аудит/доступ) живут в генераторе — здесь проверяется оркестрация.
/// </summary>
public sealed class ChatServiceTests
{
    private static GroundedResponse Response(
        string answer, short classification, IReadOnlyList<RetrievedChunk>? fragments = null) =>
        new(answer, new GroundingResult([], AllConfirmed: true), fragments ?? Array.Empty<RetrievedChunk>(), classification);

    [Fact(DisplayName = "Чат: новый диалог создаётся, сохраняются реплика пользователя и грунтованный ответ")]
    public async Task Send_creates_conversation_and_saves_both_turns()
    {
        var store = Substitute.For<IConversationStore>();
        store.CreateAsync(42, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(7);
        store.GetHistoryAsync(7, 42, Arg.Any<CancellationToken>()).Returns(new List<ChatTurn>());
        store.AppendMessageAsync(7, 42, Arg.Any<ConversationMessageRole>(), Arg.Any<string>(), Arg.Any<short>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var generator = Substitute.For<IGroundedGenerator>();
        generator.GenerateAsync(Arg.Any<GroundedRequest>(), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>())
            .Returns(Response("ответ", classification: 2));

        var service = new ChatService(generator, Substitute.For<IConversationalGenerator>(), store);

        var reply = await service.SendAsync(new ChatMessageRequest(null, "вопрос"), new AccessContext("42", 2, [7]));

        reply.ConversationId.ShouldBe(7);
        reply.Answer.ShouldBe("ответ");
        reply.Classification.ShouldBe<short>(2);
        await store.Received(1).AppendMessageAsync(7, 42, ConversationMessageRole.User, "вопрос", Arg.Any<short>(), null, Arg.Any<CancellationToken>());
        await store.Received(1).AppendMessageAsync(7, 42, ConversationMessageRole.Assistant, "ответ", (short)2, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Чат: история диалога передаётся в генератор (многоходовость)")]
    public async Task Send_passes_history_to_generator()
    {
        var store = Substitute.For<IConversationStore>();
        store.GetHistoryAsync(5, 42, Arg.Any<CancellationToken>())
            .Returns(new List<ChatTurn> { new(ConversationMessageRole.User, "прошлый вопрос") });
        store.AppendMessageAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<ConversationMessageRole>(), Arg.Any<string>(), Arg.Any<short>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        GroundedRequest? captured = null;
        var generator = Substitute.For<IGroundedGenerator>();
        generator.GenerateAsync(Arg.Do<GroundedRequest>(r => captured = r), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>())
            .Returns(Response("ответ", 0));

        var service = new ChatService(generator, Substitute.For<IConversationalGenerator>(), store);

        await service.SendAsync(new ChatMessageRequest(5, "новый вопрос"), new AccessContext("42", 2, [7]));

        captured.ShouldNotBeNull();
        captured!.History.ShouldNotBeNull();
        captured.History!.Count.ShouldBe(1);
        captured.History[0].Text.ShouldBe("прошлый вопрос");
    }

    [Fact(DisplayName = "Чат: использованные фрагменты передаются в ChatReply.UsedFragments — источники для UI (этап 7.2 Э4-35)")]
    public async Task Send_passes_used_fragments_to_reply()
    {
        var store = Substitute.For<IConversationStore>();
        store.CreateAsync(42, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(7);
        store.GetHistoryAsync(7, 42, Arg.Any<CancellationToken>()).Returns(new List<ChatTurn>());
        store.AppendMessageAsync(7, 42, Arg.Any<ConversationMessageRole>(), Arg.Any<string>(), Arg.Any<short>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var fragments = new[] { new RetrievedChunk(1, 100, "текст фрагмента", 0, 7, true, 0.1) };
        var generator = Substitute.For<IGroundedGenerator>();
        generator.GenerateAsync(Arg.Any<GroundedRequest>(), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>())
            .Returns(Response("ответ", 0, fragments));

        var service = new ChatService(generator, Substitute.For<IConversationalGenerator>(), store);

        var reply = await service.SendAsync(new ChatMessageRequest(null, "вопрос"), new AccessContext("42", 2, [7]));

        reply.UsedFragments.ShouldBe(fragments);
    }

    [Fact(DisplayName = "Чат-стриминг: использованные фрагменты передаются в финальный ChatReply (этап 7.2 Э4-35)")]
    public async Task SendStreaming_passes_used_fragments_to_final_reply()
    {
        var store = Substitute.For<IConversationStore>();
        store.CreateAsync(42, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(7);
        store.GetHistoryAsync(7, 42, Arg.Any<CancellationToken>()).Returns(new List<ChatTurn>());
        store.AppendMessageAsync(7, 42, Arg.Any<ConversationMessageRole>(), Arg.Any<string>(), Arg.Any<short>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var fragments = new[] { new RetrievedChunk(1, 100, "текст фрагмента", 0, 7, true, 0.1) };
        var generator = Substitute.For<IGroundedGenerator>();
        generator.GenerateStreamingAsync(Arg.Any<GroundedRequest>(), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>())
            .Returns(_ => DraftStreamWithFragments(fragments));

        var service = new ChatService(generator, Substitute.For<IConversationalGenerator>(), store);

        ChatReply? final = null;
        await foreach (var update in service.SendStreamingAsync(new ChatMessageRequest(null, "вопрос"), new AccessContext("42", 2, [7])))
        {
            if (update.Final is { } reply)
            {
                final = reply;
            }
        }

        final.ShouldNotBeNull();
        final!.UsedFragments.ShouldBe(fragments);
    }

    [Fact(DisplayName = "Чат fail-closed: чужой диалог — отказ ДО обращения к модели")]
    public async Task Send_rejects_foreign_conversation_before_model()
    {
        var store = Substitute.For<IConversationStore>();
        store.GetHistoryAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(new List<ChatTurn>());
        store.AppendMessageAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<ConversationMessageRole>(), Arg.Any<string>(), Arg.Any<short>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(false); // диалог субъекту не принадлежит

        var generator = Substitute.For<IGroundedGenerator>();
        var service = new ChatService(generator, Substitute.For<IConversationalGenerator>(), store);

        await Should.ThrowAsync<InvalidOperationException>(
            () => service.SendAsync(new ChatMessageRequest(999, "вопрос"), new AccessContext("42", 2, [7])));

        await generator.DidNotReceive().GenerateAsync(Arg.Any<GroundedRequest>(), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Чат: без числового субъекта — отказ (диалог требует владельца)")]
    public async Task Send_requires_numeric_subject()
    {
        var service = new ChatService(
            Substitute.For<IGroundedGenerator>(), Substitute.For<IConversationalGenerator>(), Substitute.For<IConversationStore>());

        await Should.ThrowAsync<InvalidOperationException>(
            () => service.SendAsync(new ChatMessageRequest(null, "вопрос"), new AccessContext("dev", 0, [])));
    }

    [Fact(DisplayName = "Чат-стриминг: черновик по кускам, затем Final; сохранены реплика пользователя и полный ответ")]
    public async Task SendStreaming_yields_deltas_then_final_and_saves()
    {
        var store = Substitute.For<IConversationStore>();
        store.CreateAsync(42, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(7);
        store.GetHistoryAsync(7, 42, Arg.Any<CancellationToken>()).Returns(new List<ChatTurn>());
        store.AppendMessageAsync(7, 42, Arg.Any<ConversationMessageRole>(), Arg.Any<string>(), Arg.Any<short>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var generator = Substitute.For<IGroundedGenerator>();
        generator.GenerateStreamingAsync(Arg.Any<GroundedRequest>(), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>())
            .Returns(_ => DraftStream());

        var service = new ChatService(generator, Substitute.For<IConversationalGenerator>(), store);

        var deltas = new List<string>();
        ChatReply? final = null;
        await foreach (var update in service.SendStreamingAsync(new ChatMessageRequest(null, "вопрос"), new AccessContext("42", 2, [7])))
        {
            if (update.TextDelta is { } delta)
            {
                deltas.Add(delta);
            }
            else if (update.Final is { } reply)
            {
                final = reply;
            }
        }

        deltas.ShouldBe(["Слу", "жебная"]);
        final.ShouldNotBeNull();
        final!.ConversationId.ShouldBe(7);
        final.Answer.ShouldBe("Служебная");
        final.Classification.ShouldBe<short>(2);

        await store.Received(1).AppendMessageAsync(7, 42, ConversationMessageRole.User, "вопрос", Arg.Any<short>(), null, Arg.Any<CancellationToken>());
        await store.Received(1).AppendMessageAsync(7, 42, ConversationMessageRole.Assistant, "Служебная", (short)2, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Чат-стриминг СВОБОДНЫЙ режим: из свободного генератора, без грунтовки (Grounding=null), гриф 0")]
    public async Task SendStreaming_free_mode_uses_conversational_generator_without_grounding()
    {
        var store = Substitute.For<IConversationStore>();
        store.CreateAsync(42, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(7);
        store.GetHistoryAsync(7, 42, Arg.Any<CancellationToken>()).Returns(new List<ChatTurn>());
        store.AppendMessageAsync(7, 42, Arg.Any<ConversationMessageRole>(), Arg.Any<string>(), Arg.Any<short>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var grounded = Substitute.For<IGroundedGenerator>();
        var conversational = Substitute.For<IConversationalGenerator>();
        conversational.GenerateStreamingAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<ChatTurn>>(), Arg.Any<AccessContext>(), Arg.Any<ModelRole>(), Arg.Any<CancellationToken>())
            .Returns(_ => FreeStream());

        var service = new ChatService(grounded, conversational, store);

        var deltas = new List<string>();
        ChatReply? final = null;
        await foreach (var update in service.SendStreamingAsync(new ChatMessageRequest(null, "привет", ChatMode.Free), new AccessContext("42", 2, [7])))
        {
            if (update.TextDelta is { } delta)
            {
                deltas.Add(delta);
            }
            else if (update.Final is { } reply)
            {
                final = reply;
            }
        }

        deltas.ShouldBe(["При", "вет!"]);
        final.ShouldNotBeNull();
        final!.Answer.ShouldBe("Привет!");
        final.Grounding.ShouldBeNull();          // свободный режим — не сверялось с НПА
        final.Classification.ShouldBe<short>(0); // обращения к ДСП нет

        _ = grounded.DidNotReceive().GenerateStreamingAsync(Arg.Any<GroundedRequest>(), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>());
        await store.Received(1).AppendMessageAsync(7, 42, ConversationMessageRole.Assistant, "Привет!", (short)0, null, Arg.Any<CancellationToken>());
    }

    private static async IAsyncEnumerable<string> FreeStream()
    {
        yield return "При";
        yield return "вет!";
        await Task.Yield();
    }

    private static async IAsyncEnumerable<GroundedStreamUpdate> DraftStream()
    {
        yield return new GroundedStreamUpdate("Слу", Final: null);
        yield return new GroundedStreamUpdate("жебная", Final: null);
        await Task.Yield();
        yield return new GroundedStreamUpdate(
            TextDelta: null,
            Final: new GroundedResponse("Служебная", new GroundingResult([], AllConfirmed: true), Array.Empty<RetrievedChunk>(), 2));
    }

    private static async IAsyncEnumerable<GroundedStreamUpdate> DraftStreamWithFragments(
        IReadOnlyList<RetrievedChunk> fragments)
    {
        yield return new GroundedStreamUpdate(
            TextDelta: null,
            Final: new GroundedResponse("ответ", new GroundingResult([], AllConfirmed: true), fragments, 0));
        await Task.Yield();
    }
}
