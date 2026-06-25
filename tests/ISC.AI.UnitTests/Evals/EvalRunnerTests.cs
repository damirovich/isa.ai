using ISC.AI.Abstractions.Retrieval;
using ISC.AI.Abstractions.Security;
using ISC.AI.Evals;
using NSubstitute;
using Shouldly;

namespace ISC.AI.UnitTests.Evals;

/// <summary>Прогон эталона через ретривер (Э4-05): recall@k считается раздельно по языку.</summary>
public sealed class EvalRunnerTests
{
    private static RetrievedChunk Chunk(int id) =>
        new(ChunkId: id, DocumentId: 1, Text: "текст", Classification: 0, DivisionId: 7, IsCurrent: true, Score: 0);

    [Fact(DisplayName = "EvalRunner: recall@k через ретривер, раздельно ru/ky")]
    public async Task Runs_recall_per_language()
    {
        var access = new AccessContext("u", MaxClassification: 0, AllowedDivisions: [7]);
        var retriever = Substitute.For<IRetriever>();
        retriever.RetrieveAsync("ru-q", access, Arg.Any<int>(), Arg.Any<RetrievalFilter?>(), Arg.Any<CancellationToken>())
            .Returns(new[] { Chunk(1), Chunk(3) });
        retriever.RetrieveAsync("ky-q", access, Arg.Any<int>(), Arg.Any<RetrievalFilter?>(), Arg.Any<CancellationToken>())
            .Returns(new[] { Chunk(5) });

        IReadOnlyList<EvalCase> cases =
        [
            new("ru-q", "ru", [1, 2]),
            new("ky-q", "ky", [5]),
        ];

        var report = await new EvalRunner(retriever).RunRecallAsync(cases, access, k: 10);

        report.First(r => r.Language == "ru").RecallAtK.ShouldBe(0.5); // {1,3} ∩ {1,2} = 1 / 2
        report.First(r => r.Language == "ky").RecallAtK.ShouldBe(1.0); // {5} ∩ {5} = 1 / 1
    }
}
