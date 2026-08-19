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
/// Картотека НПА (ТФ-НПА-02) на настоящем PostgreSQL: создание с уникальностью номера, реестр
/// с отбором, редакции, привязка документа корпуса и — ГЛАВНОЕ — полный цикл GATE-3: связка
/// «редакция ↔ чанки» делает смену статуса действенной (утратившая силу гасит чанки корпуса).
/// Требуется Docker.
/// </summary>
public sealed class NormRegistryStoreTests : IAsyncLifetime
{
    // Полный допуск (гриф 10, подразделение 10) — проверяется картотека, не разграничение;
    // решётка на реквизиты документов проверяется отдельным субъектом ниже.
    private static readonly AccessContext FullAccess = new("42", 10, [10]);

    // Субъект без допуска к подразделению 10 — для проверки решётки на реквизиты (ТБ-020/021).
    private static readonly AccessContext ForeignAccess = new("43", 10, [99]);

    private readonly PostgreSqlContainer _postgres = TestPostgres.Create();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "Картотека: создание (дубль номера — отказ), реестр, редакция, привязка корпуса, гашение чанков")]
    public async Task Registry_full_lifecycle()
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

        var store = new NormRegistryStore(inspectorFactory, coreFactory, new ChunkCurrencyPort(coreFactory));

        // 1) Создание: норма создаётся, повтор номера отклоняется (номер — ключ грунтовки).
        var (created, normId) = await store.CreateAsync("КЗ-59", "Закон о нормативных правовых актах");
        created.ShouldBe(NormWriteResult.Ok);
        (await store.CreateAsync("КЗ-59", "Дубль")).Result.ShouldBe(NormWriteResult.DuplicateIdentifier);

        // 2) Реестр: находится по тексту; без редакций — статус пуст.
        var page = await store.ListAsync(new NormListFilter(Text: "нормативных"));
        page.TotalCount.ShouldBe(1);
        page.Rows[0].Identifier.ShouldBe("КЗ-59");
        page.Rows[0].CurrentStatus.ShouldBeNull();

        // 3) Редакция: создаётся действующей.
        var (revisionAdded, revisionId) = await store.AddRevisionAsync(normId, new DateOnly(2026, 1, 1));
        revisionAdded.ShouldBe(NormWriteResult.Ok);
        (await store.ListAsync(new NormListFilter(Status: RevisionStatus.Active))).TotalCount.ShouldBe(1);

        // 4) Документ корпуса — настоящим конвейером загрузки (чанки с эмбеддингами, IsCurrent=true).
        var port = new IngestionPort(coreFactory, new FixedEmbeddingGenerator(768), new SimpleTextChunker());
        var ingested = await port.IngestAsync(new IngestionRequest(
            "закон", "Закон КР О НПА", "Статья 1. Общие положения.\n\nСтатья 2. Термины и определения.",
            Classification: 0, DivisionId: 10));
        ingested.Accepted.ShouldBeTrue();
        var coreDocumentId = ingested.DocumentId!.Value;

        // 5) Кандидаты на привязку: документ находится, после привязки — исчезает из кандидатов.
        (await store.SearchCorpusDocumentsAsync(normId, "НПА", FullAccess))
            .ShouldContain(c => c.DocumentId == coreDocumentId);

        // Решётка на кандидатов (ТБ-020/021): субъект чужого подразделения документа не видит.
        (await store.SearchCorpusDocumentsAsync(normId, "НПА", ForeignAccess))
            .ShouldNotContain(c => c.DocumentId == coreDocumentId);

        var (linked, linkedChunks) = await store.LinkDocumentAsync(normId, revisionId, coreDocumentId);
        linked.ShouldBe(NormWriteResult.Ok);
        linkedChunks.ShouldBeGreaterThan(0);
        (await store.LinkDocumentAsync(normId, revisionId, coreDocumentId)).Result.ShouldBe(NormWriteResult.AlreadyLinked);
        (await store.SearchCorpusDocumentsAsync(normId, "НПА", FullAccess))
            .ShouldNotContain(c => c.DocumentId == coreDocumentId);

        // Карточка видит и редакцию с фрагментами, и живой привязанный документ; субъекту вне
        // допуска реквизиты документа не отдаются — только «документ №N» (решётка, ТБ-020/021).
        var details = await store.GetAsync(normId, FullAccess);
        details!.Revisions[0].ChunkCount.ShouldBe(linkedChunks);
        details.Documents.ShouldContain(d => d.DocumentId == coreDocumentId && d.IsAlive && d.Title != null);
        var foreignDetails = await store.GetAsync(normId, ForeignAccess);
        foreignDetails!.Documents.ShouldContain(d => d.DocumentId == coreDocumentId && d.Title == null && d.IsAlive);

        // 6) ГЛАВНОЕ (GATE-3): «утратила силу» через материализатор гасит привязанные чанки ядра
        //    и проставляет дату утраты силы.
        var materializer = new RevisionStatusMaterializer(
            inspectorFactory, coreFactory, new ChunkCurrencyPort(coreFactory));
        var affected = await materializer.SetStatusAsync(revisionId, RevisionStatus.Repealed);
        affected.ShouldBe(linkedChunks);
        await using (var core = coreFactory.CreateDbContext())
        {
            (await core.Chunks.Where(c => c.DocumentId == coreDocumentId).AllAsync(c => !c.IsCurrent)).ShouldBeTrue();
        }

        (await store.GetAsync(normId, FullAccess))!.Revisions[0].RepealedDate.ShouldNotBeNull();

        // Реестр теперь видит норму как утратившую силу.
        (await store.ListAsync(new NormListFilter(Status: RevisionStatus.Repealed))).TotalCount.ShouldBe(1);
        (await store.ListAsync(new NormListFilter(Status: RevisionStatus.Active))).TotalCount.ShouldBe(0);

        // 7) Чужая редакция: привязка к редакции другой нормы отклоняется.
        var (_, otherNormId) = await store.CreateAsync("КЗ-1", "Другой закон");
        (await store.LinkDocumentAsync(otherNormId, revisionId, coreDocumentId)).Result.ShouldBe(NormWriteResult.NotFound);

        // 8) Документ ДОКУМЕНТООБОРОТА (Source = CorpusSources.DocFlow) — не нормативный материал:
        //    не предлагается кандидатом и не привязывается даже прямым вызовом (мимо диалога).
        var docflowIngested = await port.IngestAsync(new IngestionRequest(
            "поручение", "П-7 · Проверить склад", "Поручаю провести проверку склада до 1 сентября.",
            Classification: 0, DivisionId: 10, Source: CorpusSources.DocFlow));
        var docflowDocumentId = docflowIngested.DocumentId!.Value;
        (await store.SearchCorpusDocumentsAsync(otherNormId, "склад", FullAccess))
            .ShouldNotContain(c => c.DocumentId == docflowDocumentId);
        var (_, otherRevisionId) = await store.AddRevisionAsync(otherNormId, new DateOnly(2026, 2, 1));
        (await store.LinkDocumentAsync(otherNormId, otherRevisionId, docflowDocumentId)).Result
            .ShouldBe(NormWriteResult.NotFound);
    }

    [Fact(DisplayName = "GATE-3 на привязке: документ, привязанный к УТРАТИВШЕЙ СИЛУ редакции, гаснет сразу (hide-first)")]
    public async Task Linking_to_repealed_revision_hides_chunks_immediately()
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

        var store = new NormRegistryStore(inspectorFactory, coreFactory, new ChunkCurrencyPort(coreFactory));
        var materializer = new RevisionStatusMaterializer(
            inspectorFactory, coreFactory, new ChunkCurrencyPort(coreFactory));

        // Норма со СТАРОЙ редакцией, уже утратившей силу (штатный сценарий: старый текст
        // привязывают к старой редакции ПОСЛЕ того, как она погашена).
        var (_, normId) = await store.CreateAsync("КЗ-2", "Закон с историей редакций");
        var (_, oldRevisionId) = await store.AddRevisionAsync(normId, new DateOnly(2020, 1, 1));
        await materializer.SetStatusAsync(oldRevisionId, RevisionStatus.Repealed);

        // Старый текст в корпусе — свежезагружен, чанки IsCurrent=true.
        var port = new IngestionPort(coreFactory, new FixedEmbeddingGenerator(768), new SimpleTextChunker());
        var ingested = await port.IngestAsync(new IngestionRequest(
            "закон", "Старая редакция закона", "Статья 1 в прежней формулировке.",
            Classification: 0, DivisionId: 10));
        var documentId = ingested.DocumentId!.Value;

        // Привязка к погашенной редакции обязана погасить чанки СРАЗУ — иначе старый текст
        // остался бы в выдаче как действующий до следующей смены статуса.
        var (linked, linkedChunks) = await store.LinkDocumentAsync(normId, oldRevisionId, documentId);
        linked.ShouldBe(NormWriteResult.Ok);
        linkedChunks.ShouldBeGreaterThan(0);
        await using (var core = coreFactory.CreateDbContext())
        {
            (await core.Chunks.Where(c => c.DocumentId == documentId).AllAsync(c => !c.IsCurrent)).ShouldBeTrue();
        }
    }
}
