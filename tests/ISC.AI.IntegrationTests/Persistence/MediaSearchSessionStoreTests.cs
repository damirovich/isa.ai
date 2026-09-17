using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Data;
using ISC.AI.Modules.Media.Data.Entities;
using ISC.AI.Modules.Media.Domain.Model;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Testcontainers.PostgreSql;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Хранилище поисковых сессий (ТО-инф-12, ТФ-ПЛ-07, ТБ-073): сессия и кандидат-лист пишутся одной
/// транзакцией, читаются под решёткой (чужое подразделение / гриф выше допуска — null/пусто), очередь
/// верификации отбирается по стадии и делам, решение и статус меняются атомарно и не дублируются.
/// </summary>
/// <remarks>Требуется Docker.</remarks>
[Trait("Category", "Gate")]
public sealed class MediaSearchSessionStoreTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = TestPostgres.Create();

    private static readonly AccessContext Insider = new("10", MaxClassification: 1, AllowedDivisions: [7]);
    private static readonly AccessContext Outsider = new("11", MaxClassification: 9, AllowedDivisions: [8]);

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "Сессия с кандидатами создаётся атомарно; читается только в своём подразделении; кандидат берёт вырезку у лица")]
    public async Task Create_and_read_session_under_access_filter()
    {
        var factory = new MediaContextFactory(_postgres.GetConnectionString());
        int faceA, faceB;
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
            faceA = await SeedFaceAsync(db, classification: 1, divisionId: 7, crop: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.jpg");
            faceB = await SeedFaceAsync(db, classification: 1, divisionId: 7, crop: null);
        }

        var store = new SearchSessionStore(factory, new AllowAllAccessPolicy());
        var sessionId = await store.CreateAsync(
            Draft(caseId: 100, caseIds: [100, 101]),
            [Candidate(faceA, 0.10), Candidate(faceB, 0.25)]);

        var session = await store.GetAsync(sessionId, Insider);
        session.ShouldNotBeNull();
        session.CaseId.ShouldBe(100);
        session.CaseIds.ShouldBe([100, 101]);
        session.AuthorizationRef.ShouldBe("поручение № 7");
        session.Scope.ShouldBe(SearchScopeKind.SelectedCases);
        session.CandidateCount.ShouldBe(2);
        session.ProbeSha256.ShouldBe(new string('A', 64));

        var candidates = await store.ListCandidatesAsync(sessionId, Insider);
        candidates.Count.ShouldBe(2);
        candidates[0].Rank.ShouldBe(1);
        candidates[0].FaceId.ShouldBe(faceA);
        candidates[0].CropStoredFileName.ShouldBe("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.jpg");
        candidates[0].CaseId.ShouldBe(100);
        candidates[0].Status.ShouldBe(CandidateStatus.Candidate);
        candidates[0].Decisions.ShouldBeEmpty();
        candidates[1].Rank.ShouldBe(2);
        candidates[1].CropStoredFileName.ShouldBeNull();

        (await store.ListByCaseAsync(100, Insider)).ShouldHaveSingleItem().Id.ShouldBe(sessionId);
        (await store.ListByCaseAsync(101, Insider)).ShouldBeEmpty();

        // Чужое подразделение (даже с высоким допуском): сессии как будто нет (ТБ-020/021).
        (await store.GetAsync(sessionId, Outsider)).ShouldBeNull();
        (await store.ListByCaseAsync(100, Outsider)).ShouldBeEmpty();
        (await store.ListCandidatesAsync(sessionId, Outsider)).ShouldBeEmpty();
        (await store.GetCandidateAsync(candidates[0].Id, Outsider)).ShouldBeNull();

        // Без контекста — отказ, не чтение без фильтра.
        await Should.ThrowAsync<AccessContextRequiredException>(() => store.GetAsync(sessionId, null!));

        // Без подразделения сессия не создаётся (ТБ-024/070).
        await Should.ThrowAsync<ArgumentException>(
            () => store.CreateAsync(Draft(caseId: 100, caseIds: [100]) with { DivisionId = 0 }, []));
    }

    [Fact(DisplayName = "Кандидат выше допуска скрыт даже в доступной сессии; сессия выше допуска скрывает своих кандидатов")]
    public async Task Candidate_and_session_are_both_filtered()
    {
        var factory = new MediaContextFactory(_postgres.GetConnectionString());
        int openFace, secretFace;
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
            openFace = await SeedFaceAsync(db, classification: 1, divisionId: 7, crop: null);
            secretFace = await SeedFaceAsync(db, classification: 2, divisionId: 7, crop: null);
        }

        var store = new SearchSessionStore(factory, new AllowAllAccessPolicy());
        var openSession = await store.CreateAsync(
            Draft(caseId: 100, caseIds: [100]),
            [Candidate(openFace, 0.1), Candidate(secretFace, 0.2, classification: 2)]);
        var secretSession = await store.CreateAsync(
            Draft(caseId: 100, caseIds: [100]) with { Classification = 2 },
            [Candidate(openFace, 0.1)]);

        // В открытой сессии кандидат с грифом 2 субъекту с допуском 1 не виден.
        var visible = await store.ListCandidatesAsync(openSession, Insider);
        visible.ShouldHaveSingleItem().FaceId.ShouldBe(openFace);

        // Кандидат открытого носителя в секретной сессии скрыт: гриф дела — гриф сессии (ТБ-070).
        (await store.ListCandidatesAsync(secretSession, Insider)).ShouldBeEmpty();
        (await store.GetAsync(secretSession, Insider)).ShouldBeNull();

        // С допуском 2 видно всё.
        var cleared = Insider with { MaxClassification = 2 };
        (await store.ListCandidatesAsync(openSession, cleared)).Count.ShouldBe(2);
        (await store.ListCandidatesAsync(secretSession, cleared)).Count.ShouldBe(1);

        // Очередь эксперта — по делам и в допуске: только открытый кандидат открытой сессии.
        var queue = await store.ListQueueAsync(VerificationStage.Expert, [100], Insider);
        queue.ShouldHaveSingleItem().FaceId.ShouldBe(openFace);
        (await store.ListQueueAsync(VerificationStage.Expert, [100], cleared)).Count.ShouldBe(3);
    }

    [Fact(DisplayName = "ТБ-073: очередь по стадиям; решение и статус меняются одной транзакцией; повтор стадии отклоняется без следа")]
    public async Task Queue_and_decisions_follow_two_person_rule()
    {
        var factory = new MediaContextFactory(_postgres.GetConnectionString());
        int face;
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
            face = await SeedFaceAsync(db, classification: 1, divisionId: 7, crop: null);
        }

        var store = new SearchSessionStore(factory, new AllowAllAccessPolicy());
        var sessionId = await store.CreateAsync(Draft(caseId: 100, caseIds: [100]), [Candidate(face, 0.1)]);
        var candidateId = (await store.ListCandidatesAsync(sessionId, Insider)).Single().Id;

        // Стадия «эксперт»: кандидат в очереди своего дела; чужие дела — пусто; пустая область — пусто.
        (await store.ListQueueAsync(VerificationStage.Expert, [100], Insider)).ShouldHaveSingleItem();
        (await store.ListQueueAsync(VerificationStage.Expert, [999], Insider)).ShouldBeEmpty();
        (await store.ListQueueAsync(VerificationStage.Expert, [], Insider)).ShouldBeEmpty();
        (await store.ListQueueAsync(VerificationStage.Verifier, [100], Insider)).ShouldBeEmpty();

        // Решение эксперта → статус PendingVerifier: уходит из очереди эксперта, появляется у верификатора.
        var expert = new VerificationDecision(10, VerificationStage.Expert, VerificationVerdict.Confirmed, "форма ушей", DateTime.UtcNow);
        await store.RecordDecisionAsync(candidateId, expert, CandidateStatus.PendingVerifier, personRef: null);

        (await store.ListQueueAsync(VerificationStage.Expert, [100], Insider)).ShouldBeEmpty();
        var pending = (await store.ListQueueAsync(VerificationStage.Verifier, [100], Insider)).ShouldHaveSingleItem();
        pending.Status.ShouldBe(CandidateStatus.PendingVerifier);
        pending.Decisions.ShouldHaveSingleItem().SubjectId.ShouldBe(10);
        pending.PersonRef.ShouldBeNull();

        // Решение верификатора с привязкой к фигуранту → Confirmed + PersonRef.
        var verifier = new VerificationDecision(11, VerificationStage.Verifier, VerificationVerdict.Confirmed, "совпадает", DateTime.UtcNow);
        await store.RecordDecisionAsync(candidateId, verifier, CandidateStatus.Confirmed, personRef: 5);

        var confirmed = await store.GetCandidateAsync(candidateId, Insider);
        confirmed.ShouldNotBeNull();
        confirmed.Status.ShouldBe(CandidateStatus.Confirmed);
        confirmed.PersonRef.ShouldBe(5);
        confirmed.Decisions.Select(d => d.Stage).ShouldBe([VerificationStage.Expert, VerificationStage.Verifier]);
        (await store.ListQueueAsync(VerificationStage.Verifier, [100], Insider)).ShouldBeEmpty();

        // Повторное решение той же стадии — отклонено базой (уникальный индекс), и статус НЕ изменился:
        // обновление статуса и вставка решения — одна транзакция, откатывается целиком.
        var duplicate = verifier with { SubjectId = 12, Verdict = VerificationVerdict.Rejected };
        await Should.ThrowAsync<DbUpdateException>(
            () => store.RecordDecisionAsync(candidateId, duplicate, CandidateStatus.Rejected, personRef: null));

        var after = await store.GetCandidateAsync(candidateId, Insider);
        after.ShouldNotBeNull();
        after.Status.ShouldBe(CandidateStatus.Confirmed);
        after.PersonRef.ShouldBe(5);
        after.Decisions.Count.ShouldBe(2);

        // Решение по несуществующему кандидату — ошибка, следа в решениях нет.
        await Should.ThrowAsync<InvalidOperationException>(
            () => store.RecordDecisionAsync(999_999, expert, CandidateStatus.PendingVerifier, null));
        await using (var db = factory.CreateDbContext())
        {
            (await db.VerificationDecisions.CountAsync()).ShouldBe(2);
        }
    }

    private static SearchSessionDraft Draft(int caseId, int[] caseIds) => new(
        CaseId: caseId,
        AuthorizationRef: "поручение № 7",
        Scope: SearchScopeKind.SelectedCases,
        CaseIds: caseIds,
        ProbeSha256: new string('A', 64),
        ProbeFaceId: null,
        ProbeCropStoredFileName: null,
        TopK: 20,
        MaxCosineDistance: 0.6,
        DetectorVersion: "yunet-test",
        EmbedderVersion: "sface-test",
        HnswEfSearch: 200,
        Classification: 1,
        DivisionId: 7,
        RequestedByUserId: 10);

    private static FaceCandidate Candidate(int faceId, double distance, short classification = 1) =>
        new(faceId, AssetId: faceId, FrameIndex: null, FrameTimestampMs: null, distance, classification, DivisionId: 7, "sface-test");

    private static async Task<int> SeedFaceAsync(MediaDbContext db, short classification, int divisionId, string? crop)
    {
        var asset = new MediaAsset
        {
            Kind = MediaKind.Image,
            OriginalFileName = "a.jpg",
            StoredFileName = Guid.NewGuid().ToString("N") + ".jpg",
            ContentType = "image/jpeg",
            ContentHash = Guid.NewGuid().ToString("N"),
            ByteSize = 1,
            Classification = classification,
            DivisionId = divisionId,
            IndexStatus = MediaIndexStatus.Indexed,
        };
        db.Assets.Add(asset);
        await db.SaveChangesAsync();

        var face = new Face
        {
            AssetId = asset.Id,
            BoxX = 1, BoxY = 1, BoxWidth = 50, BoxHeight = 50,
            Landmarks = new float[10],
            DetectionScore = 0.95f,
            QualityScore = 0.9f,
            QualityAcceptable = true,
            CropStoredFileName = crop,
            Classification = classification,
            DivisionId = divisionId,
        };
        db.Faces.Add(face);
        await db.SaveChangesAsync();
        return face.Id;
    }
}
