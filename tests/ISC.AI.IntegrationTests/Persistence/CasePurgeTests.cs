using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Enums;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Data;
using ISC.AI.Modules.Media.Data.Entities;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using ISC.AI.Persistence.Audit;
using ISC.AI.Profile.Investigation.Application.Features.Cases;
using ISC.AI.Profile.Investigation.Data;
using ISC.AI.Profile.Investigation.Domain.Entities;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Pgvector;
using Shouldly;
using Testcontainers.PostgreSql;
using Xunit;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Уничтожение дела целиком на настоящей базе (ADR-0025, ТБ-064, GATE-6): три схемы (core, media,
/// investigation) в одном контейнере, настоящие каскады внешних ключей, настоящий журнал аудита.
/// </summary>
/// <remarks>
/// Главное, что здесь проверяется и чего не видно в юнит-тестах, — ГРАНИЦА уничтожения. Дело A
/// уничтожается; у него есть носитель, общий с делом B (дедупликация по хешу), и дело B искало по лицам
/// дела A. После уничтожения A: всё «своё» у A исчезает физически, а общий носитель, привязка B к нему,
/// сессия поиска B и документ документооборота — на месте. Стереть материалы соседнего дела — самая
/// тяжёлая ошибка, которую может сделать эта операция.
/// </remarks>
[Trait("Category", "Gate")]
public sealed class CasePurgeTests : IAsyncLifetime
{
    private const int AdminId = 1;

    private readonly PostgreSqlContainer _postgres = TestPostgres.Create();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "ADR-0025: дело уничтожено целиком, общий носитель и история соседнего дела целы, документ сохранён, акт в журнале — первым")]
    public async Task Destroys_case_and_keeps_neighbour_materials()
    {
        var connection = _postgres.GetConnectionString();
        var investigation = new InvestigationContextFactory(connection);
        var media = new MediaContextFactory(connection);
        var core = new CoreContextFactory(connection);

        await using (var db = core.CreateDbContext())
        {
            await db.Database.MigrateAsync(); // журнал аудита — в схеме ядра
        }

        await using (var db = media.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        await InvestigationTestKit.MigrateAsync(investigation);
        await InvestigationTestKit.AssignRolesAsync(investigation, (AdminId, InvestigationRole.Administrator));

        var cases = InvestigationTestKit.CreateCaseStore(investigation, core);
        var persons = InvestigationTestKit.CreatePersonStore(investigation, core);
        var scope = InvestigationTestKit.CreateCaseScope(investigation, core);
        var admin = InvestigationTestKit.Access(AdminId, 9, 5);

        var caseA = await cases.CreateAsync(InvestigationTestKit.Draft("A-2026/1", 5, 2, AdminId), admin);
        var caseB = await cases.CreateAsync(InvestigationTestKit.Draft("B-2026/1", 5, 2, AdminId), admin);

        // Носители: own — только дела A; shared — и A, и B (дедупликация по хешу); other — только B.
        int own, shared, other;
        await using (var db = media.CreateDbContext())
        {
            own = await SeedVideoAsync(db, "own");
            shared = await SeedVideoAsync(db, "shared");
            other = await SeedVideoAsync(db, "other");
        }

        await scope.LinkAssetAsync(caseA.CaseId, own, null, AdminId);
        await scope.LinkAssetAsync(caseA.CaseId, shared, null, AdminId);
        await scope.LinkAssetAsync(caseB.CaseId, shared, null, AdminId);
        await scope.LinkAssetAsync(caseB.CaseId, other, null, AdminId);

        // Дело A: фигурант с эталоном и появлением, основание поиска, привязанный документ, поиск по лицам.
        var person = await persons.CreateAsync(new PersonDraft(caseA.CaseId, "Фигурант А", false, null, null), admin);
        (await cases.AddAuthorizationAsync(
            new SearchAuthorizationDraft(caseA.CaseId, AuthorizationKind.InvestigatorOrder, "Поручение № 1", new DateOnly(2026, 9, 1), AdminId, null, null),
            admin)).Result.ShouldBe(CaseWriteResult.Ok);

        int ownFace;
        await using (var db = media.CreateDbContext())
        {
            ownFace = await db.Faces.Where(f => f.AssetId == own).Select(f => f.Id).FirstAsync();
            await SeedSessionAsync(db, caseA.CaseId, candidateAssetId: own, candidateFaceId: ownFace, probeFile: "probe-a.jpg");

            // Дело B искало и нашло лицо из носителя дела A — это история ДЕЛА B, её трогать нельзя.
            await SeedSessionAsync(db, caseB.CaseId, candidateAssetId: own, candidateFaceId: ownFace, probeFile: "probe-b.jpg");
        }

        await using (var db = investigation.CreateDbContext())
        {
            db.ReferencePhotos.Add(new ReferencePhoto
            {
                PersonId = person.PersonId, MediaAssetId = own, MediaFaceId = ownFace, Classification = 2, DivisionId = 5,
            });
            db.CaseDocumentLinks.Add(new CaseDocumentLink { CaseId = caseA.CaseId, DocFlowDocumentId = 900 });
            await db.SaveChangesAsync();
        }

        await scope.RecordAppearanceAsync(new ConfirmedAppearance(
            caseA.CaseId, person.PersonId, SessionId: 1, CandidateId: 1, FaceId: ownFace, AssetId: own,
            FrameIndex: 0, FrameTimestampMs: 0, Similarity: 0.9, Classification: 2, DivisionId: 5,
            ExpertUserId: 40, VerifierUserId: 41, ConfirmedAtUtc: DateTime.UtcNow));

        // --- Уничтожение дела A настоящими компонентами ---
        var storage = new RecordingFileStorage();
        var audit = new AuditWriter(core);
        var accessProvider = Substitute.For<IAccessContextProvider>();
        accessProvider.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(admin);

        var handler = new PurgeCaseCommand.Handler(
            cases, new UserRoleStore(core, investigation), new FixedSubjectProvider(AdminId), accessProvider,
            new MediaPurger(media, audit, storage), audit);

        var response = await handler.Handle(
            new PurgeCaseCommand(caseA.CaseId, "A-2026/1", "Решение руководителя от 20.09.2026 № 17"), CancellationToken.None);

        response.Status.ShouldBeTrue(response.StatusMessage);
        var summary = response.Data.ShouldNotBeNull();
        summary.AssetsRemoved.ShouldBe(1);
        summary.AssetsKeptShared.ShouldBe(1);
        summary.SessionsRemoved.ShouldBe(1);
        summary.DocumentsKept.ShouldBe(1);

        // --- Дело A исчезло целиком ---
        await using (var db = investigation.CreateDbContext())
        {
            (await db.Cases.AnyAsync(c => c.Id == caseA.CaseId)).ShouldBeFalse();
            (await db.Persons.AnyAsync(p => p.CaseId == caseA.CaseId)).ShouldBeFalse();
            (await db.ReferencePhotos.AnyAsync(r => r.PersonId == person.PersonId)).ShouldBeFalse();
            (await db.Appearances.AnyAsync(a => a.CaseId == caseA.CaseId)).ShouldBeFalse();
            (await db.SearchAuthorizations.AnyAsync(a => a.CaseId == caseA.CaseId)).ShouldBeFalse();
            (await db.CaseMediaLinks.AnyAsync(l => l.CaseId == caseA.CaseId)).ShouldBeFalse();

            // Привязка документа снята, а сам документ документооборота не трогался (его в этой схеме нет).
            (await db.CaseDocumentLinks.AnyAsync(l => l.CaseId == caseA.CaseId)).ShouldBeFalse();

            // --- Дело B не пострадало: запись, обе его привязки на месте ---
            (await db.Cases.AnyAsync(c => c.Id == caseB.CaseId)).ShouldBeTrue();
            (await db.CaseMediaLinks.Where(l => l.CaseId == caseB.CaseId).Select(l => l.MediaAssetId).OrderBy(id => id).ToListAsync())
                .ShouldBe(new[] { shared, other }.Order().ToList());
        }

        await using (var db = media.CreateDbContext())
        {
            // Свой носитель A — физически, со всеми производными.
            (await db.Assets.AnyAsync(a => a.Id == own)).ShouldBeFalse();
            (await db.Frames.AnyAsync(f => f.AssetId == own)).ShouldBeFalse();
            (await db.Faces.AnyAsync(f => f.AssetId == own)).ShouldBeFalse();
            (await db.Templates.AnyAsync(t => t.AssetId == own)).ShouldBeFalse();

            // Общий носитель и носитель B — целы вместе с биометрией: это материалы дела B.
            (await db.Templates.CountAsync(t => t.AssetId == shared)).ShouldBe(2);
            (await db.Templates.CountAsync(t => t.AssetId == other)).ShouldBe(2);

            // История поисков: сессия A ушла с кандидатами, сессия B осталась — это история чужого дела.
            (await db.SearchSessions.AnyAsync(s => s.CaseId == caseA.CaseId)).ShouldBeFalse();
            var sessionB = await db.SearchSessions.SingleAsync(s => s.CaseId == caseB.CaseId);
            (await db.SearchCandidates.CountAsync(c => c.SessionId == sessionB.Id)).ShouldBe(1);
        }

        // Файлы: исходник и вырезки своего носителя + вырезка пробы A; пробы B и файлы общего носителя — нет.
        storage.Deleted.ShouldContain($"{MediaFileCategories.Originals}/{own}/own.mp4");
        storage.Deleted.ShouldContain($"{MediaFileCategories.Probes}/{caseA.CaseId}/probe-a.jpg");
        storage.Deleted.ShouldNotContain(f => f.Contains("probe-b", StringComparison.Ordinal));
        storage.Deleted.ShouldNotContain(f => f.Contains($"/{shared}/", StringComparison.Ordinal));

        // Журнал: акт — ПЕРВАЯ запись уничтожения (fail-closed), за ним носитель и история поисков.
        await using (var db = core.CreateDbContext())
        {
            var purges = await db.AuditRecords
                .Where(r => r.Action == AuditAction.Purge)
                .OrderBy(r => r.Id)
                .Select(r => r.ObjectRef)
                .ToListAsync();

            purges.ShouldBe([
                $"investigation:case:{caseA.CaseId}:destroy",
                $"media:asset:{own}",
                $"media:case:{caseA.CaseId}:searches"]);
        }
    }

    // Видео с двумя кадрами, на каждом — лицо с вырезкой и шаблоном. Гриф 2, подразделение 5.
    private static async Task<int> SeedVideoAsync(MediaDbContext db, string name)
    {
        var asset = new MediaAsset
        {
            Kind = MediaKind.Video,
            OriginalFileName = name + ".mp4",
            StoredFileName = name + ".mp4",
            ContentType = "video/mp4",
            ContentHash = Guid.NewGuid().ToString("N"),
            ByteSize = 1,
            DurationMs = 2000,
            Classification = 2,
            DivisionId = 5,
            IndexStatus = MediaIndexStatus.Indexed,
        };
        db.Assets.Add(asset);
        await db.SaveChangesAsync();

        for (var i = 0; i < 2; i++)
        {
            var frame = new MediaFrame { AssetId = asset.Id, Index = i, TimestampMs = i * 1000 };
            db.Frames.Add(frame);
            await db.SaveChangesAsync();

            var face = new Face
            {
                AssetId = asset.Id,
                FrameId = frame.Id,
                BoxX = 1, BoxY = 1, BoxWidth = 40, BoxHeight = 40,
                Landmarks = new float[10],
                DetectionScore = 0.9f,
                QualityScore = 0.8f,
                QualityAcceptable = true,
                CropStoredFileName = Guid.NewGuid().ToString("N") + ".jpg",
                Classification = 2,
                DivisionId = 5,
            };
            db.Faces.Add(face);
            await db.SaveChangesAsync();

            var values = new float[FaceTemplate.Dimensions];
            values[i] = 1f;
            db.Templates.Add(new FaceTemplate
            {
                FaceId = face.Id,
                AssetId = asset.Id,
                Embedding = new Vector(values),
                ModelVersion = "sface-test",
                QualityAcceptable = true,
                Classification = 2,
                DivisionId = 5,
            });
            await db.SaveChangesAsync();
        }

        return asset.Id;
    }

    // Поисковая сессия дела с одним кандидатом и вырезкой пробы.
    private static async Task SeedSessionAsync(
        MediaDbContext db, int caseId, int candidateAssetId, int candidateFaceId, string probeFile)
    {
        var session = new SearchSession
        {
            CaseId = caseId,
            AuthorizationRef = "Поручение № 1",
            Scope = SearchScopeKind.CurrentCase,
            CaseIds = [caseId],
            ProbeSha256 = Guid.NewGuid().ToString("N"),
            ProbeCropStoredFileName = probeFile,
            TopK = 20,
            DetectorVersion = "yunet-test",
            EmbedderVersion = "sface-test",
            HnswEfSearch = 200,
            Classification = 2,
            DivisionId = 5,
            RequestedByUserId = AdminId,
        };
        db.SearchSessions.Add(session);
        await db.SaveChangesAsync();

        db.SearchCandidates.Add(new SearchCandidate
        {
            SessionId = session.Id,
            Rank = 1,
            FaceId = candidateFaceId,
            AssetId = candidateAssetId,
            CosineDistance = 0.1,
            ModelVersion = "sface-test",
            Classification = 2,
            DivisionId = 5,
        });
        await db.SaveChangesAsync();
    }
}
