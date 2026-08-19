using ISC.AI.Abstractions.Ingestion;
using ISC.AI.Abstractions.Security;
using ISC.AI.Ingestion;
using ISC.AI.Persistence.Corpus;
using ISC.AI.Profile.Inspector.Data;
using ISC.AI.Profile.Inspector.Domain.Enums;
using ISC.AI.Profile.Inspector.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Testcontainers.PostgreSql;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Каталог корпуса НПА (ТФ-НПА-01/02) на настоящем PostgreSQL: решётка допуска в запросе (ТБ-020/021),
/// статус действия по флагу годности чанков (GATE-3), отбор и сортировка, документооборот не в каталоге,
/// карточка с фрагментами и пометкой утративших силу. Требуется Docker.
/// </summary>
public sealed class CorpusCatalogTests : IAsyncLifetime
{
    private static readonly AccessContext FullAccess = new("42", 10, [10]);
    private static readonly AccessContext ForeignAccess = new("43", 10, [99]);
    private static readonly AccessContext LowClearance = new("44", 0, [10]);

    private readonly PostgreSqlContainer _postgres = TestPostgres.Create();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "Каталог: решётка допуска, статус по чанкам, фильтры/сортировка, без документооборота, карточка")]
    public async Task Catalog_lists_visible_documents_with_currency_and_details()
    {
        var connectionString = _postgres.GetConnectionString();
        var inspectorFactory = new InspectorContextFactory(connectionString);
        var coreFactory = new CoreContextFactory(connectionString);
        await using (var db = inspectorFactory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        await using (var db = coreFactory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        var port = new IngestionPort(coreFactory, new FixedEmbeddingGenerator(768), new SimpleTextChunker());
        var catalog = new CorpusCatalog(coreFactory, inspectorFactory);

        // Четыре документа: открытый закон, кодекс ДСП-2, поручение документооборота, закон чужого подразделения.
        var law = (await port.IngestAsync(new IngestionRequest(
            "закон", "Закон о нормативных правовых актах", "Статья 1. Общие положения.\n\nСтатья 2. Термины.",
            Classification: 0, DivisionId: 10, DocDate: new DateOnly(2021, 5, 5)))).DocumentId!.Value;
        var code = (await port.IngestAsync(new IngestionRequest(
            "кодекс", "Кодекс о правонарушениях", "Глава 1. Основы.\n\nГлава 2. Составы.",
            Classification: 2, DivisionId: 10, DocDate: new DateOnly(2019, 1, 1)))).DocumentId!.Value;
        var docflow = (await port.IngestAsync(new IngestionRequest(
            "поручение", "П-7 · Проверить склад", "Поручаю провести проверку склада.",
            Classification: 0, DivisionId: 10, Source: CorpusSources.DocFlow))).DocumentId!.Value;
        await port.IngestAsync(new IngestionRequest(
            "закон", "Закон чужого подразделения", "Текст для другого подразделения.",
            Classification: 0, DivisionId: 99));

        // 1) Полный допуск: закон и кодекс; поручение документооборота и чужое подразделение — нет.
        var page = await catalog.ListAsync(new CorpusCatalogFilter(), FullAccess);
        page.TotalCount.ShouldBe(2);
        page.Rows.Select(r => r.DocumentId).ShouldBe([law, code]); // DateDesc: 2021 раньше 2019
        page.Rows.ShouldAllBe(r => r.Currency == CorpusDocumentCurrency.Current);
        page.DocTypes.ShouldBe(["закон", "кодекс"]);
        page.Rows.ShouldNotContain(r => r.DocumentId == docflow);

        // 2) Решётка: гриф ниже — только открытый закон; чужое подразделение — пусто (fail-closed).
        (await catalog.ListAsync(new CorpusCatalogFilter(), LowClearance)).Rows
            .Select(r => r.DocumentId).ShouldBe([law]);
        // Субъект подразделения 99 видит ТОЛЬКО свой документ — ни закона, ни кодекса подразделения 10.
        var foreignPage = await catalog.ListAsync(new CorpusCatalogFilter(), ForeignAccess);
        foreignPage.Rows.Single().Title.ShouldBe("Закон чужого подразделения");

        // 3) Отбор и сортировка.
        (await catalog.ListAsync(new CorpusCatalogFilter(DocType: "кодекс"), FullAccess)).Rows
            .Single().DocumentId.ShouldBe(code);
        (await catalog.ListAsync(new CorpusCatalogFilter(Text: "правонаруш"), FullAccess)).Rows
            .Single().DocumentId.ShouldBe(code);
        (await catalog.ListAsync(new CorpusCatalogFilter(Sort: CorpusCatalogSort.DateAsc), FullAccess)).Rows
            .Select(r => r.DocumentId).ShouldBe([code, law]);

        // 4) GATE-3 в каталоге: кодекс привязан к утратившей силу редакции → статус «Утратил силу»,
        //    фильтр по статусу его находит, карточка помечает каждый фрагмент.
        var norms = new NormRegistryStore(inspectorFactory, coreFactory, new ChunkCurrencyPort(coreFactory));
        var materializer = new RevisionStatusMaterializer(inspectorFactory, coreFactory, new ChunkCurrencyPort(coreFactory));
        var (_, normId) = await norms.CreateAsync("КоАП", "Кодекс о правонарушениях (норма)");
        var (_, revisionId) = await norms.AddRevisionAsync(normId, new DateOnly(2019, 1, 1));
        (await norms.LinkDocumentAsync(normId, revisionId, code)).Result.ShouldBe(NormWriteResult.Ok);
        await materializer.SetStatusAsync(revisionId, RevisionStatus.Repealed);

        var superseded = await catalog.ListAsync(
            new CorpusCatalogFilter(Currency: CorpusDocumentCurrency.Superseded), FullAccess);
        superseded.Rows.Single().DocumentId.ShouldBe(code);
        (await catalog.ListAsync(new CorpusCatalogFilter(Currency: CorpusDocumentCurrency.Current), FullAccess)).Rows
            .Single().DocumentId.ShouldBe(law);

        var details = await catalog.GetAsync(code, FullAccess);
        details!.Currency.ShouldBe(CorpusDocumentCurrency.Superseded);
        details.Fragments.Count.ShouldBeGreaterThan(0);
        details.Fragments.ShouldAllBe(f => !f.IsCurrent);
        details.LinkedNorms.Single().Identifier.ShouldBe("КоАП");

        // 5) Карточка вне допуска — null, неотличимо от несуществующего.
        (await catalog.GetAsync(code, LowClearance)).ShouldBeNull();
        (await catalog.GetAsync(docflow, FullAccess)).ShouldBeNull();
    }
}
