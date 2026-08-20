using System.Net;
using System.Text;
using ISC.AI.Abstractions.Harvesting;
using ISC.AI.Harvester.Connectors;
using ISC.AI.Harvester.Engine;
using Shouldly;

namespace ISC.AI.UnitTests.Harvester;

/// <summary>
/// API-коннектор ЦБД Минюста (Э4-16) на канонных ответах: в пакет попадают ТОЛЬКО действующие акты,
/// текст берётся из <c>contentRu</c> (полный текст), утратившие силу отсеиваются (fail-closed по статусу).
/// </summary>
public sealed class CbdApiConnectorTests
{
    [Fact(DisplayName = "ЦБД API: только «Действует» + текст акта из contentRu; утратившие силу отсеяны")]
    public async Task Harvests_active_only_with_body()
    {
        using var client = new HttpClient(new CbdApiHandler());
        var connector = new CbdApiConnector(client, new HtmlContentExtractor());
        var config = new SourceConfig("https://cbd.minjust.gov.kg", "нпа", Classification: 0, MaxDocuments: 10);

        var docs = new List<HarvestedDocument>();
        await foreach (var doc in connector.HarvestAsync(config))
        {
            docs.Add(doc);
        }

        docs.Count.ShouldBe(1); // из двух документов взят только «Действует»
        var d = docs[0];
        d.Title.ShouldBe("Активный закон");
        d.Text.ShouldContain("Статья 1");
        d.Text.ShouldContain("Текст активного закона");
        d.Text.ShouldNotContain("MsoNormal"); // Word-стили сняты извлекателем
        d.DocType.ShouldBe("закон");
        d.DivisionId.ShouldBeNull(); // подразделение выбирается при импорте внутри контура
        d.Language.ShouldBe("ru");
        d.SourceUrl.ShouldBe("https://cbd.minjust.gov.kg/1-1/edition/100/ru");
        d.Metadata!["status"].ShouldBe("Действует");
        d.Metadata["editionId"].ShouldBe("100");
    }

    private sealed class CbdApiHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            string json;
            if (url.Contains("/GetDocuments", StringComparison.Ordinal))
            {
                // Страница 1 — два документа (действующий + утративший силу); дальше — пусто (конец пагинации).
                json = url.Contains("pageNumber=1", StringComparison.Ordinal)
                    ? """
                      {"data":[
                        {"documentCode":"1-1","nameRu":"Активный закон","status":"Действует","vid":"закон","lastEdition":100},
                        {"documentCode":"1-2","nameRu":"Старый закон","status":"Утратил силу","vid":"закон","lastEdition":200}
                      ]}
                      """
                    : """{"data":[]}""";
            }
            else if (url.Contains("editionId=100", StringComparison.Ordinal))
            {
                json = """{"contentRu":"<html><head><style>p.MsoNormal{margin:0}</style></head><body><p class=\"MsoNormal\">Статья 1. Текст активного закона.</p></body></html>","contentKg":""}""";
            }
            else
            {
                json = """{"contentRu":"","contentKg":""}""";
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }
}
