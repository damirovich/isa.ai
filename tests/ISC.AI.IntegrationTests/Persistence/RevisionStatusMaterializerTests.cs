using ISC.AI.Persistence;
using ISC.AI.Persistence.Corpus;
using ISC.AI.Persistence.Entities;
using ISC.AI.Profile.Inspector.Data;
using ISC.AI.Profile.Inspector.Domain.Entities;
using ISC.AI.Profile.Inspector.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Testcontainers.PostgreSql;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Write-side GATE-3 (Э4-02, ADR-0013): при переводе редакции НПА в «утратила силу» нейтральный флаг
/// годности связанных чанков ядра гаснет (по `ChunkRevisionLink`), и они перестают выдаваться как
/// действующие. Две схемы (core+inspector) в ОДНОЙ БД через Testcontainers.
/// </summary>
[Trait("Category", "Gate")]
public sealed class RevisionStatusMaterializerTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = TestPostgres.Create();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "GATE-3 (write): редакция → утратила силу гасит is_current связанных чанков ядра")]
    public async Task Repealing_revision_hides_linked_core_chunks()
    {
        var coreFactory = new CoreContextFactory(_postgres.GetConnectionString());
        var inspectorFactory = new InspectorContextFactory(_postgres.GetConnectionString());

        int chunkA, chunkB, revisionId;

        await using (var core = coreFactory.CreateDbContext())
        {
            await core.Database.MigrateAsync();
            var document = new DocumentEntity { DocType = "закон", Title = "З", Classification = 0, DivisionId = 7 };
            core.Documents.Add(document);
            await core.SaveChangesAsync();

            var a = new ChunkEntity { DocumentId = document.Id, Ordinal = 0, Text = "a", Classification = 0, DivisionId = 7, IsCurrent = true };
            var b = new ChunkEntity { DocumentId = document.Id, Ordinal = 1, Text = "b", Classification = 0, DivisionId = 7, IsCurrent = true };
            core.Chunks.AddRange(a, b);
            await core.SaveChangesAsync();
            chunkA = a.Id;
            chunkB = b.Id;
        }

        await using (var inspector = inspectorFactory.CreateDbContext())
        {
            await inspector.Database.MigrateAsync();
            var norm = new LegalNorm { Identifier = "N-1", Title = "Норма" };
            inspector.LegalNorms.Add(norm);
            await inspector.SaveChangesAsync();

            var revision = new NormRevision { NormId = norm.Id, Status = RevisionStatus.Active, EffectiveDate = new DateOnly(2026, 1, 1) };
            inspector.NormRevisions.Add(revision);
            await inspector.SaveChangesAsync();
            revisionId = revision.Id;

            inspector.ChunkRevisionLinks.AddRange(
                new ChunkRevisionLink { NormRevisionId = revision.Id, ChunkId = chunkA },
                new ChunkRevisionLink { NormRevisionId = revision.Id, ChunkId = chunkB });
            await inspector.SaveChangesAsync();
        }

        var materializer = new RevisionStatusMaterializer(
            inspectorFactory, coreFactory, new ChunkCurrencyPort(coreFactory));
        var affected = await materializer.SetStatusAsync(revisionId, RevisionStatus.Repealed);

        affected.ShouldBe(2);

        await using var coreVerify = coreFactory.CreateDbContext();
        var chunks = await coreVerify.Chunks.Where(c => c.Id == chunkA || c.Id == chunkB).ToListAsync();
        chunks.ShouldAllBe(c => !c.IsCurrent); // связанные чанки скрыты (write-side GATE-3)

        await using var inspectorVerify = inspectorFactory.CreateDbContext();
        (await inspectorVerify.NormRevisions.FirstAsync(r => r.Id == revisionId)).Status.ShouldBe(RevisionStatus.Repealed);
    }
}
