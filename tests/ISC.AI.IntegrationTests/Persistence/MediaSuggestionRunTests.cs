using System;
using System.Linq;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Data;
using ISC.AI.Modules.Media.Data.Entities;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using ISC.AI.Persistence.Audit;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Testcontainers.PostgreSql;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Журнал сверок и предложения системы на карточке носителя (ТФ-ПЕР-09, ADR-0035) на настоящем Postgres: последняя
/// сверка по делу — под решёткой и только по делам из области; предложения — только «предложено системой» этого
/// носителя, фигурант — только до решения эксперта; журнал сверок уходит с носителем (каскад) и с делом (уничтожение).
/// </summary>
/// <remarks>Требуется Docker.</remarks>
public sealed class MediaSuggestionRunTests : IAsyncLifetime
{
    private static readonly AccessContext Insider = new("10", MaxClassification: 1, AllowedDivisions: [7]);

    private readonly PostgreSqlContainer _postgres = TestPostgres.Create();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "Сверки носителя: по делу — последняя; выше допуска, чужое подразделение и дела вне области не читаются; удалён носитель — сверок нет")]
    public async Task Latest_run_per_case_under_access_and_cascade()
    {
        var factory = new MediaContextFactory(_postgres.GetConnectionString());
        int assetId, otherAssetId;
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
            assetId = await SeedAssetAsync(db);
            otherAssetId = await SeedAssetAsync(db);
        }

        var store = new SuggestionRunStore(factory, new AllowAllAccessPolicy());
        await store.RecordAsync(Run(otherAssetId, caseId: 7, candidates: 0));
        await store.RecordAsync(Run(assetId, caseId: 7, candidates: 0));
        await store.RecordAsync(Run(assetId, caseId: 7, candidates: 2) with { Trigger = SuggestionTrigger.CaseSweep });
        await store.RecordAsync(Run(assetId, caseId: 8, candidates: 1) with { Classification = 3 }); // выше допуска
        await store.RecordAsync(Run(assetId, caseId: 9, candidates: 1) with { DivisionId = 8 });     // чужое подразделение
        await using (var db = factory.CreateDbContext())
        {
            // Первая сверка дела 7 — заведомо раньше второй: порядок не зависит от разрешения часов.
            var first = await db.SuggestionRuns.Where(r => r.CaseId == 7 && r.AssetId == assetId).OrderBy(r => r.Id).Select(r => r.Id).FirstAsync();
            await db.SuggestionRuns.Where(r => r.Id == first)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.CreatedAt, DateTime.UtcNow.AddHours(-1)));
        }

        var latest = (await store.ListLatestAsync(assetId, [7, 8, 9], Insider)).ShouldHaveSingleItem();
        latest.CaseId.ShouldBe(7);
        latest.CandidatesCreated.ShouldBe(2);
        latest.Trigger.ShouldBe(SuggestionTrigger.CaseSweep);
        latest.CreatedAtUtc.Kind.ShouldBe(DateTimeKind.Utc);
        (await store.ListLatestAsync(assetId, [8, 9], Insider)).ShouldBeEmpty();
        (await store.ListLatestAsync(assetId, [], Insider)).ShouldBeEmpty();

        await using (var db = factory.CreateDbContext())
        {
            await db.Assets.Where(a => a.Id == assetId).ExecuteDeleteAsync();
            (await db.SuggestionRuns.Select(r => r.AssetId).Distinct().ToListAsync()).ShouldBe([otherAssetId]); // каскад — только этого носителя
        }
    }

    [Fact(DisplayName = "Предложения на носителе: только «предложено системой», этого носителя и дел области; фигурант виден лишь до решения эксперта")]
    public async Task Suggested_for_asset_hides_person_after_expert()
    {
        var factory = new MediaContextFactory(_postgres.GetConnectionString());
        int face;
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
            face = await SeedFaceAsync(db);
        }

        var store = new SearchSessionStore(factory, new AllowAllAccessPolicy());
        const int assetId = 555;
        var suggestion = await store.CreateAsync(
            Session(caseId: 100) with { Origin = SessionOrigin.SystemSuggestion, SuggestedPersonRef = 9, ProbeFaceId = 42 },
            [new FaceCandidate(face, assetId, null, null, 0.2, 1, 7, "sface-test")]);
        await store.CreateAsync(Session(caseId: 100) with { ProbeFaceId = 43 },                // поиск сотрудника — не предложение
            [new FaceCandidate(face, assetId, null, null, 0.1, 1, 7, "sface-test")]);
        await store.CreateAsync(
            Session(caseId: 101) with { Origin = SessionOrigin.SystemSuggestion, SuggestedPersonRef = 9, ProbeFaceId = 44 }, // другое дело
            [new FaceCandidate(face, assetId, null, null, 0.2, 1, 7, "sface-test")]);
        await store.CreateAsync(
            Session(caseId: 100) with { Origin = SessionOrigin.SystemSuggestion, SuggestedPersonRef = 9, ProbeFaceId = 45 }, // другой носитель
            [new FaceCandidate(face, assetId + 1, null, null, 0.2, 1, 7, "sface-test")]);

        var row = (await store.ListSuggestedForAssetAsync(assetId, [100], Insider)).ShouldHaveSingleItem();
        row.SessionId.ShouldBe(suggestion);
        row.CaseId.ShouldBe(100);
        row.FaceId.ShouldBe(face);
        row.Similarity.ShouldBe(0.8, 1e-9);
        row.Status.ShouldBe(CandidateStatus.Candidate);
        row.SuggestedPersonRef.ShouldBe(9);
        (await store.ListSuggestedForAssetAsync(assetId, [], Insider)).ShouldBeEmpty();

        await store.RecordDecisionAsync(
            row.CandidateId,
            new VerificationDecision(10, VerificationStage.Expert, VerificationVerdict.Confirmed, "форма ушей", DateTime.UtcNow),
            CandidateStatus.PendingVerifier,
            personRef: 9);

        var pending = (await store.ListSuggestedForAssetAsync(assetId, [100], Insider)).ShouldHaveSingleItem();
        pending.Status.ShouldBe(CandidateStatus.PendingVerifier);
        pending.SuggestedPersonRef.ShouldBeNull(); // слепая проекция верификатора (ТФ-ВЕР-02)
    }

    [Fact(DisplayName = "Уничтожение дела (ADR-0025): сверки дела удаляются и без истории поисков; сверки другого дела остаются")]
    public async Task Case_purge_removes_case_runs()
    {
        var media = new MediaContextFactory(_postgres.GetConnectionString());
        var core = new CoreContextFactory(_postgres.GetConnectionString());
        int assetId;
        await using (var db = media.CreateDbContext())
        {
            await db.Database.MigrateAsync();
            assetId = await SeedAssetAsync(db);
        }

        await using (var db = core.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        var runs = new SuggestionRunStore(media, new AllowAllAccessPolicy());
        await runs.RecordAsync(Run(assetId, caseId: 7, candidates: 0));
        await runs.RecordAsync(Run(assetId, caseId: 8, candidates: 0));

        var result = await new MediaPurger(media, new AuditWriter(core), new RecordingFileStorage())
            .PurgeCaseSearchesAsync(7, "уничтожение дела", subjectId: 42);

        result.ShouldBe(CaseSearchPurgeResult.Empty); // поисков у дела не было
        await using (var db = media.CreateDbContext())
        {
            (await db.SuggestionRuns.Select(r => r.CaseId).ToListAsync()).ShouldBe([8]);
        }
    }

    private static SuggestionRunDraft Run(int assetId, int caseId, int candidates) =>
        new(assetId, caseId, SuggestionTrigger.Indexing, 1, candidates > 0 ? 1 : 0, candidates, 0.5, 1, 7);

    private static SearchSessionDraft Session(int caseId) => new(
        caseId,
        "поручение № 7",
        SearchScopeKind.CurrentCase,
        [caseId],
        new string('a', 64),
        null,
        null,
        5,
        0.5,
        "yunet-test",
        "sface-test",
        200,
        1,
        7,
        RequestedByUserId: null);

    private static async Task<int> SeedAssetAsync(MediaDbContext db)
    {
        var asset = new MediaAsset
        {
            Kind = MediaKind.Image,
            OriginalFileName = "a.jpg",
            StoredFileName = Guid.NewGuid().ToString("N") + ".jpg",
            ContentType = "image/jpeg",
            ContentHash = Guid.NewGuid().ToString("N"),
            ByteSize = 1,
            Classification = 1,
            DivisionId = 7,
            IndexStatus = MediaIndexStatus.Indexed,
        };
        db.Assets.Add(asset);
        await db.SaveChangesAsync();
        return asset.Id;
    }

    private static async Task<int> SeedFaceAsync(MediaDbContext db)
    {
        var assetId = await SeedAssetAsync(db);
        var face = new Face
        {
            AssetId = assetId,
            BoxX = 1, BoxY = 1, BoxWidth = 50, BoxHeight = 50,
            Landmarks = new float[10],
            DetectionScore = 0.95f,
            QualityScore = 0.9f,
            QualityAcceptable = true,
            Classification = 1,
            DivisionId = 7,
        };
        db.Faces.Add(face);
        await db.SaveChangesAsync();
        return face.Id;
    }
}
