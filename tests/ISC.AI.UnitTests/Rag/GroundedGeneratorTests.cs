using ISC.AI.AI.Rag;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Enums;
using ISC.AI.Abstractions.Grounding;
using ISC.AI.Abstractions.Rag;
using ISC.AI.Abstractions.Retrieval;
using ISC.AI.Abstractions.Security;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
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

        var generator = new GroundedGenerator(retriever, grounding, audit, provider);
        var access = new AccessContext("u1", MaxClassification: 2, AllowedDivisions: [7]);

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

        // Генерация аудирована (ТБ-030).
        await audit.Received(1).WriteAsync(Arg.Is<AuditEntry>(e => e.Action == AuditAction.Generate), Arg.Any<CancellationToken>());

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
            new ServiceCollection().BuildServiceProvider());

        await Should.ThrowAsync<ArgumentNullException>(
            () => generator.GenerateAsync(new GroundedRequest("вопрос"), access: null!));
    }
}
