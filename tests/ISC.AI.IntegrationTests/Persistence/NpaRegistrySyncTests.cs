using ISC.AI.Abstractions.Ingestion;
using ISC.AI.Ingestion;
using ISC.AI.Persistence.Corpus;
using ISC.AI.Profile.Inspector.Data;
using ISC.AI.Profile.Inspector.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Testcontainers.PostgreSql;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Автонаполнение картотеки из корпуса (закрытие «Осталось» Э4-02) на настоящем PostgreSQL:
/// нормы/редакции/связки из метаданных ЦБД, идемпотентность, автогашение прежней редакции при
/// приходе новой (GATE-3), неприкосновенность ручных редакций и документов без метаданных.
/// Требуется Docker.
/// </summary>
public sealed class NpaRegistrySyncTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = TestPostgres.Create();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "Синхронизация: норма+редакция+связки из метаданных ЦБД; новая редакция гасит прежнюю; идемпотентно")]
    public async Task Sync_builds_registry_and_repeals_stale_revisions()
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
        var materializer = new RevisionStatusMaterializer(
            inspectorFactory, coreFactory, new ChunkCurrencyPort(coreFactory));
        var synchronizer = new NpaRegistrySynchronizer(inspectorFactory, coreFactory, materializer);

        // Корпус: акт ЦБД (редакция 100), документ БЕЗ метаданных (ручная загрузка) — его не трогаем.
        var lawV1 = (await port.IngestAsync(new IngestionRequest(
            "закон", "Закон КР от 24 октября 1998 года № 135 \"О чрезвычайном положении\"",
            "Статья 1. Основные понятия.\n\nСтатья 2. Порядок введения.",
            Classification: 0, DivisionId: 1, DocDate: new DateOnly(1998, 10, 24),
            Metadata: new Dictionary<string, string>
            {
                ["documentCode"] = "3-48",
                ["editionId"] = "100",
                ["status"] = "Действует",
            }))).DocumentId!.Value;
        await port.IngestAsync(new IngestionRequest(
            "положение", "Внутреннее положение без метаданных", "Текст ручной загрузки.",
            Classification: 0, DivisionId: 1));

        // 1) Первый проход: норма по documentCode, редакция по editionId, документ и чанки привязаны.
        var first = await synchronizer.SyncFromCorpusAsync();
        first.NormsCreated.ShouldBe(1);
        first.RevisionsCreated.ShouldBe(1);
        first.DocumentsLinked.ShouldBe(1);
        first.ChunksLinked.ShouldBeGreaterThan(0);
        first.RevisionsRepealed.ShouldBe(0);

        int normId;
        int firstRevisionId;
        await using (var db = inspectorFactory.CreateDbContext())
        {
            var norm = await db.LegalNorms.Include(n => n.Revisions).SingleAsync();
            norm.Identifier.ShouldBe("3-48");
            norm.Title.ShouldStartWith("Закон КР от 24 октября 1998");
            normId = norm.Id;
            var revision = norm.Revisions.Single();
            revision.ExternalEditionId.ShouldBe("100");
            revision.Status.ShouldBe(RevisionStatus.Active);
            revision.EffectiveDate.ShouldBe(new DateOnly(1998, 10, 24));
            firstRevisionId = revision.Id;
        }

        // 2) Повтор — идемпотентен: ничего нового.
        var second = await synchronizer.SyncFromCorpusAsync();
        second.NormsCreated.ShouldBe(0);
        second.RevisionsCreated.ShouldBe(0);
        second.DocumentsLinked.ShouldBe(0);
        second.ChunksLinked.ShouldBe(0);
        second.RevisionsRepealed.ShouldBe(0);

        // 3) Пришла НОВАЯ редакция того же акта (editionId 200): новая редакция Active,
        //    прежняя автоматическая — погашена, её чанки скрыты (GATE-3).
        await port.IngestAsync(new IngestionRequest(
            "закон", "Закон КР от 24 октября 1998 года № 135 \"О чрезвычайном положении\" (ред. 2026)",
            "Статья 1. Основные понятия (обновлённые).\n\nСтатья 2. Новый порядок введения.",
            Classification: 0, DivisionId: 1, DocDate: new DateOnly(1998, 10, 24),
            Metadata: new Dictionary<string, string>
            {
                ["documentCode"] = "3-48",
                ["editionId"] = "200",
                ["status"] = "Действует",
            }));

        var third = await synchronizer.SyncFromCorpusAsync();
        third.NormsCreated.ShouldBe(0); // та же норма
        third.RevisionsCreated.ShouldBe(1);
        third.RevisionsRepealed.ShouldBe(1);

        await using (var db = inspectorFactory.CreateDbContext())
        {
            var revisions = await db.NormRevisions.Where(r => r.NormId == normId).ToListAsync();
            revisions.Count.ShouldBe(2);
            revisions.Single(r => r.ExternalEditionId == "100").Status.ShouldBe(RevisionStatus.Repealed);
            revisions.Single(r => r.ExternalEditionId == "200").Status.ShouldBe(RevisionStatus.Active);
        }

        await using (var core = coreFactory.CreateDbContext())
        {
            // Чанки старой редакции погашены, новой — действуют.
            (await core.Chunks.Where(c => c.DocumentId == lawV1).AllAsync(c => !c.IsCurrent)).ShouldBeTrue();
        }

        // 4) Ручной документ без метаданных в картотеку не попал; ручная редакция не гасится.
        await using (var db = inspectorFactory.CreateDbContext())
        {
            (await db.LegalNorms.CountAsync()).ShouldBe(1);

            // Ручная редакция (без внешнего ключа) — Active; новая автоматическая её не трогает.
            db.NormRevisions.Add(new ISC.AI.Profile.Inspector.Domain.Entities.NormRevision
            {
                NormId = normId,
                Status = RevisionStatus.Active,
                EffectiveDate = new DateOnly(2026, 1, 1),
                ExternalEditionId = null,
            });
            await db.SaveChangesAsync();
        }

        await port.IngestAsync(new IngestionRequest(
            "закон", "Закон КР № 135 (ред. 300)", "Статья 1. Ещё новее.",
            Classification: 0, DivisionId: 1,
            Metadata: new Dictionary<string, string> { ["documentCode"] = "3-48", ["editionId"] = "300" }));
        var fourth = await synchronizer.SyncFromCorpusAsync();
        fourth.RevisionsCreated.ShouldBe(1);
        fourth.RevisionsRepealed.ShouldBe(1); // погашена «200», но НЕ ручная

        await using (var db = inspectorFactory.CreateDbContext())
        {
            var manual = await db.NormRevisions.SingleAsync(r => r.ExternalEditionId == null);
            manual.Status.ShouldBe(RevisionStatus.Active);
        }
    }
}
