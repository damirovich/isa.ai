using ISC.AI.Abstractions.Harvesting;
using ISC.AI.Harvester.Connectors;
using ISC.AI.Harvester.Engine;
using Shouldly;

namespace ISC.AI.UnitTests.Harvester;

/// <summary>
/// Коннектор «сайт по правилам» (Э4-08, ADR-0015): по селекторам из конфига обходит список → карточки.
/// Источник описывается КОНФИГОМ (правилами), а не классом — проверяем на заранее заданном HTML через
/// фейковый <see cref="IPageFetcher"/> (способ получения HTML — статический/headless — за абстракцией, Э4-15).
/// </summary>
public sealed class ConfigurableSiteConnectorTests
{
    private static readonly Dictionary<string, string> Pages = new()
    {
        ["http://site/list"] =
            "<html><body><ul>" +
            "<li><a class='doc' href='/doc/1'>Док 1</a></li>" +
            "<li><a class='doc' href='/doc/2'>Док 2</a></li>" +
            "</ul></body></html>",
        ["http://site/doc/1"] =
            "<html><body><h1 class='t'>Закон 1</h1><div class='c'>Текст закона 1.</div></body></html>",
        ["http://site/doc/2"] =
            "<html><body><h1 class='t'>Закон 2</h1><div class='c'>Текст закона 2.</div></body></html>",
    };

    [Fact(DisplayName = "Сайт по правилам: список → карточки по селекторам; поля и метаданные заполнены")]
    public async Task Harvests_by_rules()
    {
        var connector = new ConfigurableSiteConnector(new FakePageFetcherFactory(Pages));
        var rules = new SiteRules(ItemLinkSelector: "a.doc", TitleSelector: "h1.t", BodySelector: "div.c", MaxPages: 1);
        var config = new SourceConfig("http://site/list", "закон", Classification: 0, DivisionId: 7, MaxDocuments: 10, Language: "ru", Rules: rules);

        var docs = new List<HarvestedDocument>();
        await foreach (var doc in connector.HarvestAsync(config))
        {
            docs.Add(doc);
        }

        docs.Count.ShouldBe(2);
        docs[0].SourceUrl.ShouldBe("http://site/doc/1");
        docs[0].Title.ShouldBe("Закон 1");
        docs[0].Text.ShouldContain("Текст закона 1.");
        docs[0].DocType.ShouldBe("закон");
        docs[0].DivisionId.ShouldBe(7);
        docs[1].Title.ShouldBe("Закон 2");
    }

    [Fact(DisplayName = "Сайт по правилам: без правил в конфиге — отказ")]
    public async Task Without_rules_throws()
    {
        var connector = new ConfigurableSiteConnector(new FakePageFetcherFactory(Pages));
        var config = new SourceConfig("http://site/list", "закон", Classification: 0, DivisionId: 7);

        await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in connector.HarvestAsync(config))
            {
            }
        });
    }

    [Fact(DisplayName = "RenderMode=Headless (Э4-15): у фабрики запрашивается headless; документы извлекаются из отрисованного HTML")]
    public async Task Headless_mode_requests_headless_fetcher_and_extracts()
    {
        var factory = new FakePageFetcherFactory(Pages);
        var rules = new SiteRules(
            ItemLinkSelector: "a.doc", TitleSelector: "h1.t", BodySelector: "div.c", MaxPages: 1,
            RenderMode: RenderMode.Headless, ReadySelector: "div.c");
        var config = new SourceConfig("http://site/list", "закон", Classification: 0, DivisionId: 7, MaxDocuments: 10, Rules: rules);

        var docs = new List<HarvestedDocument>();
        await foreach (var doc in new ConfigurableSiteConnector(factory).HarvestAsync(config))
        {
            docs.Add(doc);
        }

        // Режим headless проброшен в фабрику; извлечение работает на «отрисованном» HTML так же, как на статическом.
        factory.LastRequestedMode.ShouldBe(RenderMode.Headless);
        docs.Count.ShouldBe(2);
        docs[0].Title.ShouldBe("Закон 1");
    }

    [Fact(DisplayName = "Пресет gov.kg: список a[href*='/npa/s/'] → карточки h2.section-name-title + .section-npa, без мусора меню")]
    public async Task GovKg_preset_extracts_per_document()
    {
        var pages = new Dictionary<string, string>
        {
            ["https://www.gov.kg/ru/npa?page=1"] =
                "<html><body><nav><a href='/ru/about'>О нас</a></nav>" +
                "<a href='https://www.gov.kg/ru/npa/s/4816'>430</a>" +
                "<a href='https://www.gov.kg/ru/npa/s/4815'>421</a></body></html>",
            ["https://www.gov.kg/ru/npa/s/4816"] =
                "<html><body><h2 class='section-name-title demi-lg-black'>Постановление № 430</h2>" +
                "<div class='section-content m-form section-npa'>Текст постановления 430.</div></body></html>",
            ["https://www.gov.kg/ru/npa/s/4815"] =
                "<html><body><h2 class='section-name-title'>Постановление № 421</h2>" +
                "<div class='section-npa'>Текст постановления 421.</div></body></html>",
        };

        var preset = SitePresets.All.First(p => p.Name.Contains("gov.kg", StringComparison.Ordinal));
        var config = new SourceConfig(preset.SuggestedSeedUrl, preset.DocType, Classification: 0, DivisionId: 7, MaxDocuments: 10, Rules: preset.Rules);

        var docs = new List<HarvestedDocument>();
        await foreach (var doc in new ConfigurableSiteConnector(new FakePageFetcherFactory(pages)).HarvestAsync(config))
        {
            docs.Add(doc);
        }

        // По документу на постановление (ссылка «О нас» не подошла под селектор).
        docs.Count.ShouldBe(2);
        docs[0].SourceUrl.ShouldBe("https://www.gov.kg/ru/npa/s/4816");
        docs[0].Title.ShouldBe("Постановление № 430");
        docs[0].Text.ShouldBe("Текст постановления 430.");
        docs[0].Text.ShouldNotContain("О нас"); // меню навигации не попало
        docs[0].DocType.ShouldBe("постановление");
        docs[1].Title.ShouldBe("Постановление № 421");
    }
}
