using ISC.AI.Abstractions.Retrieval;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Inspector.Application.Search;
using NSubstitute;
using Shouldly;

namespace ISC.AI.UnitTests.Profiles;

/// <summary>
/// Поиск по НПА (Э4-07): handler зовёт ядровой <see cref="IRetriever"/> с контекстом доступа и
/// проецирует фрагменты в результат. Аудит обращения — сквозное AuditBehavior (Э4-11), проверяется отдельно.
/// </summary>
public sealed class SearchNpaHandlerTests
{
    [Fact(DisplayName = "Поиск НПА: ретривер зовётся с доступом; фрагменты замаппены в результат")]
    public async Task Searches_and_maps_hits()
    {
        var access = new AccessContext("u1", MaxClassification: 1, AllowedDivisions: [7]);
        var accessProvider = Substitute.For<IAccessContextProvider>();
        accessProvider.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(access);

        var chunks = new List<RetrievedChunk>
        {
            new(ChunkId: 10, DocumentId: 5, Text: "фрагмент A", Classification: 1, DivisionId: 7, IsCurrent: true, Score: 0.1),
            new(ChunkId: 11, DocumentId: 6, Text: "фрагмент B", Classification: 0, DivisionId: 7, IsCurrent: true, Score: 0.2),
        };
        var retriever = Substitute.For<IRetriever>();
        retriever.RetrieveAsync("режим хранения", access, Arg.Any<int>(), Arg.Any<RetrievalFilter?>(), Arg.Any<CancellationToken>())
            .Returns(chunks);

        var response = await new SearchNpaQuery.Handler(retriever, accessProvider)
            .Handle(new SearchNpaQuery("режим хранения"), CancellationToken.None);

        // Результат: фрагменты замаппены, TotalCount проставлен.
        response.Status.ShouldBeTrue();
        response.Data.ShouldNotBeNull();
        response.Data!.Hits.Count.ShouldBe(2);
        response.Data.Hits[0].DocumentId.ShouldBe(5);
        response.Data.Hits[0].Text.ShouldBe("фрагмент A");
        response.TotalCount.ShouldBe(2);
    }
}
