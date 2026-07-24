using ISC.AI.AI.Rag;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Enums;
using ISC.AI.Abstractions.Grounding;
using ISC.AI.Abstractions.Rag;
using ISC.AI.Abstractions.Retrieval;
using ISC.AI.Abstractions.Security;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;

namespace ISC.AI.UnitTests.Rag;

/// <summary>
/// Тест RAG-конвейера (ТО-мат-01): порядок и режимные инварианты — модель получает ТОЛЬКО
/// отфильтрованные фрагменты, системное правило грунтовки идёт первым, грунтовка обязательна,
/// итоговый гриф = max грифов фрагментов (наследование), генерация аудируется.
/// </summary>
public sealed class GroundedGeneratorTests
{
    private static RetrievedChunk Chunk(int id, string text, short classification) =>
        new(ChunkId: id, DocumentId: 1, Text: text, Classification: classification, DivisionId: 7, IsCurrent: true, Score: 0);

    [Fact(DisplayName = "Конвейер: retriever → модель (только его фрагменты + грунтовка) → грунтовка → аудит; гриф=max")]
    public async Task Pipeline_enforces_order_and_regime()
    {
        var fragments = new List<RetrievedChunk> { Chunk(10, "текст A", 1), Chunk(11, "текст B", 2) };

        var retriever = Substitute.For<IRetriever>();
        retriever.RetrieveAsync(Arg.Any<string>(), Arg.Any<AccessContext>(), Arg.Any<int>(), Arg.Any<RetrievalFilter?>(), Arg.Any<CancellationToken>())
            .Returns(fragments);

        List<ChatMessage>? sentMessages = null;
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(
                Arg.Do<IEnumerable<ChatMessage>>(m => sentMessages = m.ToList()),
                Arg.Any<ChatOptions?>(),
                Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ответ модели")));

        var grounding = Substitute.For<IGroundingValidator>();
        grounding.Validate(Arg.Any<string>(), Arg.Any<IReadOnlyList<RetrievedChunk>>())
            .Returns(new GroundingResult([], AllConfirmed: true));

        var audit = Substitute.For<IAuditWriter>();

        var provider = new ServiceCollection()
            .AddKeyedSingleton<IChatClient>(ModelRole.Analysis, chatClient)
            .BuildServiceProvider();

        var generator = new GroundedGenerator(
            retriever, grounding, audit, provider, GenerationOptions.Default, NullLogger<GroundedGenerator>.Instance);
        var access = new AccessContext("42", MaxClassification: 2, AllowedDivisions: [7]);

        var response = await generator.GenerateAsync(new GroundedRequest("вопрос"), access);

        // Извлечение вызвано с контекстом доступа (фильтр применён ДО модели).
        await retriever.Received(1).RetrieveAsync("вопрос", access, Arg.Any<int>(), Arg.Any<RetrievalFilter?>(), Arg.Any<CancellationToken>());

        // Модель получила системное правило грунтовки ПЕРВЫМ и только фрагменты ретривера.
        sentMessages.ShouldNotBeNull();
        sentMessages![0].Role.ShouldBe(ChatRole.System);
        sentMessages.ShouldContain(m => (m.Text ?? string.Empty).Contains("текст A"));
        sentMessages.ShouldContain(m => (m.Text ?? string.Empty).Contains("текст B"));

        // Грунтовка вызвана на выводе модели.
        grounding.Received(1).Validate("ответ модели", Arg.Any<IReadOnlyList<RetrievedChunk>>());

        // Генерация аудирована (ТБ-030 «кто/что/когда»): действие Generate, субъект из контекста доступа,
        // гриф = max грифов фрагментов, id использованных фрагментов зафиксированы.
        await audit.Received(1).WriteAsync(
            Arg.Is<AuditEntry>(e =>
                e.Action == AuditAction.Generate &&
                e.SubjectId == 42 &&
                e.Classification == 2 &&
                e.ObjectRef == "10,11"),
            Arg.Any<CancellationToken>());

        // Наследование грифа: итог = максимум грифов фрагментов.
        response.ResultClassification.ShouldBe<short>(2);
        response.Answer.ShouldBe("ответ модели");
        response.UsedFragments.Count.ShouldBe(2);
    }

    [Fact(DisplayName = "Конвейер fail-closed: без контекста доступа — отказ")]
    public async Task Null_access_throws()
    {
        var generator = new GroundedGenerator(
            Substitute.For<IRetriever>(),
            Substitute.For<IGroundingValidator>(),
            Substitute.For<IAuditWriter>(),
            new ServiceCollection().BuildServiceProvider(),
            GenerationOptions.Default,
            NullLogger<GroundedGenerator>.Instance);

        await Should.ThrowAsync<ArgumentNullException>(
            () => generator.GenerateAsync(new GroundedRequest("вопрос"), access: null!));
    }

    [Fact(DisplayName = "Стриминг: черновик отдаётся по токенам; грунтовка — на СОБРАННОМ полном тексте; итог одним Final")]
    public async Task Streaming_yields_draft_deltas_then_final_grounded_on_full_text()
    {
        var fragments = new List<RetrievedChunk> { Chunk(10, "текст A", 1), Chunk(11, "текст B", 2) };

        var retriever = Substitute.For<IRetriever>();
        retriever.RetrieveAsync(Arg.Any<string>(), Arg.Any<AccessContext>(), Arg.Any<int>(), Arg.Any<RetrievalFilter?>(), Arg.Any<CancellationToken>())
            .Returns(fragments);

        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetStreamingResponseAsync(
                Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(_ => StreamParts("Служебная ", "справка."));

        string? groundedOn = null;
        var grounding = Substitute.For<IGroundingValidator>();
        grounding.Validate(Arg.Do<string>(t => groundedOn = t), Arg.Any<IReadOnlyList<RetrievedChunk>>())
            .Returns(new GroundingResult([], AllConfirmed: true));

        var audit = Substitute.For<IAuditWriter>();

        var provider = new ServiceCollection()
            .AddKeyedSingleton<IChatClient>(ModelRole.Analysis, chatClient)
            .BuildServiceProvider();

        var generator = new GroundedGenerator(
            retriever, grounding, audit, provider, GenerationOptions.Default, NullLogger<GroundedGenerator>.Instance);
        var access = new AccessContext("42", MaxClassification: 2, AllowedDivisions: [7]);

        var updates = new List<GroundedStreamUpdate>();
        await foreach (var update in generator.GenerateStreamingAsync(new GroundedRequest("вопрос"), access))
        {
            updates.Add(update);
        }

        // Промежуточные обновления — сырой черновик по частям, БЕЗ Final.
        var deltas = updates.Where(u => u.Final is null).ToList();
        deltas.Select(u => u.TextDelta).ShouldBe(["Служебная ", "справка."]);

        // Ровно одно терминальное обновление с Final; текст черновика в нём НЕ дублируется.
        var terminal = updates.Single(u => u.Final is not null);
        terminal.TextDelta.ShouldBeNull();

        // Грунтовка выполнена на СОБРАННОМ полном тексте (а не на отдельном токене) — ключевой инвариант.
        groundedOn.ShouldBe("Служебная справка.");
        terminal.Final!.Answer.ShouldBe("Служебная справка.");

        // Наследование грифа и единственная запись аудита с субъектом — как в блокирующем пути.
        terminal.Final.ResultClassification.ShouldBe<short>(2);
        await audit.Received(1).WriteAsync(
            Arg.Is<AuditEntry>(e => e.Action == AuditAction.Generate && e.SubjectId == 42 && e.Classification == 2),
            Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "ТБ-030: сбой модели — попытка генерации зафиксирована в аудите (ДСП извлечён), исключение проброшено")]
    public async Task Failed_generation_is_audited_and_rethrows()
    {
        var fragments = new List<RetrievedChunk> { Chunk(10, "текст A", 1), Chunk(11, "текст B", 2) };

        var retriever = Substitute.For<IRetriever>();
        retriever.RetrieveAsync(Arg.Any<string>(), Arg.Any<AccessContext>(), Arg.Any<int>(), Arg.Any<RetrievalFilter?>(), Arg.Any<CancellationToken>())
            .Returns(fragments);

        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("сервер модели недоступен"));

        var audit = Substitute.For<IAuditWriter>();
        var provider = new ServiceCollection()
            .AddKeyedSingleton<IChatClient>(ModelRole.Analysis, chatClient)
            .BuildServiceProvider();

        var generator = new GroundedGenerator(
            retriever, Substitute.For<IGroundingValidator>(), audit, provider,
            GenerationOptions.Default, NullLogger<GroundedGenerator>.Instance);
        var access = new AccessContext("42", MaxClassification: 2, AllowedDivisions: [7]);

        // Модель падает → исключение проброшено вызывающему.
        await Should.ThrowAsync<InvalidOperationException>(
            () => generator.GenerateAsync(new GroundedRequest("вопрос"), access));

        // Но обращение к защищённым фрагментам зафиксировано (ТБ-030): действие Generate, субъект,
        // гриф = max(1,2) = 2, id использованных фрагментов — как «попытка», даже без ответа модели.
        await audit.Received(1).WriteAsync(
            Arg.Is<AuditEntry>(e =>
                e.Action == AuditAction.Generate &&
                e.SubjectId == 42 &&
                e.Classification == 2 &&
                e.ObjectRef == "10,11"),
            Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Параметры генерации из конфига (temperature/max_tokens/seed) передаются модели")]
    public async Task Generation_options_are_passed_to_model()
    {
        var retriever = Substitute.For<IRetriever>();
        retriever.RetrieveAsync(Arg.Any<string>(), Arg.Any<AccessContext>(), Arg.Any<int>(), Arg.Any<RetrievalFilter?>(), Arg.Any<CancellationToken>())
            .Returns(new List<RetrievedChunk> { Chunk(10, "текст", 1) });

        ChatOptions? sentOptions = null;
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Do<ChatOptions?>(o => sentOptions = o), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ответ")));

        var grounding = Substitute.For<IGroundingValidator>();
        grounding.Validate(Arg.Any<string>(), Arg.Any<IReadOnlyList<RetrievedChunk>>()).Returns(new GroundingResult([], AllConfirmed: true));

        var provider = new ServiceCollection().AddKeyedSingleton<IChatClient>(ModelRole.Analysis, chatClient).BuildServiceProvider();
        var options = new GenerationOptions(Temperature: 0.1f, MaxOutputTokens: 1234, Seed: 42);
        var generator = new GroundedGenerator(
            retriever, grounding, Substitute.For<IAuditWriter>(), provider, options, NullLogger<GroundedGenerator>.Instance);

        await generator.GenerateAsync(new GroundedRequest("вопрос"), new AccessContext("1", MaxClassification: 2, AllowedDivisions: [7]));

        sentOptions.ShouldNotBeNull();
        sentOptions!.Temperature.ShouldBe(0.1f);
        sentOptions.MaxOutputTokens.ShouldBe(1234);
        sentOptions.Seed.ShouldBe(42);
    }

    [Fact(DisplayName = "Бюджет токенов: лишние фрагменты отброшены; грунтовка — по ФАКТИЧЕСКИ отправленным")]
    public async Task Token_budget_truncates_fragments_and_grounds_on_included()
    {
        // Три больших фрагмента (лучший первым); крошечный бюджет вместит НЕ все.
        var fragments = new List<RetrievedChunk>
        {
            Chunk(10, new string('а', 400), 1),
            Chunk(11, new string('б', 400), 1),
            Chunk(12, new string('в', 400), 1),
        };
        var retriever = Substitute.For<IRetriever>();
        retriever.RetrieveAsync(Arg.Any<string>(), Arg.Any<AccessContext>(), Arg.Any<int>(), Arg.Any<RetrievalFilter?>(), Arg.Any<CancellationToken>())
            .Returns(fragments);

        List<ChatMessage>? sent = null;
        var chatClient = Substitute.For<IChatClient>();
        chatClient.GetResponseAsync(Arg.Do<IEnumerable<ChatMessage>>(m => sent = m.ToList()), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ответ")));

        IReadOnlyList<RetrievedChunk>? groundedOn = null;
        var grounding = Substitute.For<IGroundingValidator>();
        grounding.Validate(Arg.Any<string>(), Arg.Do<IReadOnlyList<RetrievedChunk>>(f => groundedOn = f)).Returns(new GroundingResult([], AllConfirmed: true));

        var provider = new ServiceCollection().AddKeyedSingleton<IChatClient>(ModelRole.Analysis, chatClient).BuildServiceProvider();
        var options = new GenerationOptions(PromptTokenBudget: 400, CharsPerToken: 2.5);
        var generator = new GroundedGenerator(
            retriever, grounding, Substitute.For<IAuditWriter>(), provider, options, NullLogger<GroundedGenerator>.Instance);

        var response = await generator.GenerateAsync(new GroundedRequest("вопрос"), new AccessContext("1", MaxClassification: 2, AllowedDivisions: [7]));

        // Отправлено меньше, чем извлечено (но хотя бы один); грунтовка и результат — по ОТПРАВЛЕННЫМ.
        response.UsedFragments.Count.ShouldBeLessThan(3);
        response.UsedFragments.Count.ShouldBeGreaterThan(0);
        groundedOn!.Count.ShouldBe(response.UsedFragments.Count);

        // В промпт вошёл лучший фрагмент, но не худший (отброшен по бюджету).
        var userText = sent!.Last().Text ?? string.Empty;
        userText.ShouldContain(new string('а', 400));
        userText.ShouldNotContain(new string('в', 400));
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> StreamParts(params string[] parts)
    {
        foreach (var part in parts)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, part);
        }

        await Task.CompletedTask;
    }
}
