using ISC.AI.Abstractions.Harvesting;
using ISC.AI.Harvester.Connectors;
using ISC.AI.Harvester.Engine;
using Shouldly;

namespace ISC.AI.UnitTests.Harvester;

/// <summary>Generic-коннектор (Э4-08): качает URL, извлекает текст, проставляет декларированные тип/гриф/подразделение.</summary>
public sealed class GenericUrlConnectorTests
{
    [Fact(DisplayName = "Generic-коннектор: один URL → документ с текстом и метаданными из конфига")]
    public async Task Harvests_single_url()
    {
        const string html = "<html><head><title>Док</title></head><body><p>Текст НПА.</p></body></html>";
        var pages = new Dictionary<string, string> { ["http://example/doc"] = html };
        var connector = new GenericUrlConnector(new FakePageFetcherFactory(pages), new HtmlContentExtractor());

        var config = new SourceConfig("http://example/doc", DocType: "положение", Classification: 0, DivisionId: 7, Language: "ru");

        var docs = new List<HarvestedDocument>();
        await foreach (var doc in connector.HarvestAsync(config))
        {
            docs.Add(doc);
        }

        docs.Count.ShouldBe(1);
        docs[0].Title.ShouldBe("Док");
        docs[0].Text.ShouldContain("Текст НПА.");
        docs[0].DocType.ShouldBe("положение");
        docs[0].Classification.ShouldBe<short>(0);
        docs[0].DivisionId.ShouldBe(7);
        docs[0].SourceUrl.ShouldBe("http://example/doc");
        docs[0].ContentHash.ShouldNotBeNullOrWhiteSpace();
    }
}
