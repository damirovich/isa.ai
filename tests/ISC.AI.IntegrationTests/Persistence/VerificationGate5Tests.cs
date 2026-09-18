using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Data;
using ISC.AI.Modules.Media.Data.Entities;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using ISC.AI.Profile.Investigation.Data;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Testcontainers.PostgreSql;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// GATE-5 (ТБ-073): правило двух лиц на НАСТОЯЩИХ портах трёх схем одной БД — роль из
/// <c>investigation.user_role_assignment</c> → <see cref="VerificationPolicy"/> → <see cref="TwoPersonRule"/> →
/// <see cref="SearchSessionStore"/> (транзакция + уникальный индекс стадии) → <see cref="CaseScope"/>
/// (фигуранты дела под решёткой, появление, страховка от самоподтверждения). Ни одной заглушки на пути
/// решения: подменяется только контекст допуска субъекта.
/// </summary>
/// <remarks>
/// Оркестрация обработчика <c>RecordVerificationCommand</c> (пакет Media.Application) здесь НЕ вызывается:
/// проект интеграционных тестов на Media.Application не ссылается. Сквозной вариант через обработчик
/// (с проверками аудита <c>core.audit_record</c> и слепоты через <c>GetSearchSessionQuery</c>) подготовлен
/// и подключается одной ссылкой проекта — см. отчёт трека T1. Требуется Docker (Postgres + pgvector);
/// контейнер — на каждый тест: сценарии независимы.
/// </remarks>
[Trait("Category", "Gate")]
public sealed class VerificationGate5Tests : IAsyncLifetime
{
    // Роли по ТП-004: 10 — следователь (инициирует поиск, решений стадий не пишет), 40 — эксперт по лицам,
    // 41 и 43 — верификаторы, 50 — без роли профиля (default-deny).
    private const int Investigator = 10;
    private const int Expert = 40;
    private const int Verifier = 41;
    private const int SecondVerifier = 43;
    private const int NoRole = 50;

    private const short CaseClassification = 1;
    private const int CaseDivision = 7;
    private const string Rationale = "форма ушной раковины и межзрачковое расстояние по методике";

    private readonly PostgreSqlContainer _postgres = TestPostgres.Create();

    private CoreContextFactory _core = null!;
    private MediaContextFactory _media = null!;
    private InvestigationContextFactory _investigation = null!;
    private SearchSessionStore _sessions = null!;
    private CaseScope _scope = null!;
    private VerificationPolicy _policy = null!;
    private int _caseId;
    private int _personId;
    private int _assetId;
    private int _faceId;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        var connectionString = _postgres.GetConnectionString();
        _core = new CoreContextFactory(connectionString);
        _media = new MediaContextFactory(connectionString);
        _investigation = new InvestigationContextFactory(connectionString);

        // Три схемы одной БД: ядро (пользователи/журнал) — core, сессии и лица — media, дела/фигуранты/роли — investigation.
        await using (var db = _core.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        await using (var db = _media.CreateDbContext())
        {
            await db.Database.MigrateAsync();
            (_assetId, _faceId) = await SeedFaceAsync(db);
        }

        await InvestigationTestKit.MigrateAsync(_investigation);
        await InvestigationTestKit.AssignRolesAsync(_investigation,
            (Investigator, InvestigationRole.Investigator),
            (Expert, InvestigationRole.FaceExpert),
            (Verifier, InvestigationRole.Verifier),
            (SecondVerifier, InvestigationRole.Verifier));

        // Дело следователя 10 (гриф 1, подразделение 7) и его фигурант — через настоящие хранилища с решёткой.
        var cases = InvestigationTestKit.CreateCaseStore(_investigation, _core);
        var persons = InvestigationTestKit.CreatePersonStore(_investigation, _core);
        var owner = Access(Investigator);

        var created = await cases.CreateAsync(InvestigationTestKit.Draft("Г5-1", CaseDivision, CaseClassification, Investigator), owner);
        created.Result.ShouldBe(CaseWriteResult.Ok);
        _caseId = created.CaseId;

        var person = await persons.CreateAsync(new PersonDraft(_caseId, "Фигурант Г5", IsUnidentified: false, null, null), owner);
        person.Result.ShouldBe(PersonWriteResult.Ok);
        _personId = person.PersonId;

        _scope = InvestigationTestKit.CreateCaseScope(_investigation, _core);
        await _scope.LinkAssetAsync(_caseId, _assetId, place: null, Investigator);

        _sessions = new SearchSessionStore(_media, new AllowAllAccessPolicy());
        _policy = new VerificationPolicy(new UserRoleStore(_core, _investigation));
    }

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "GATE-5/ТП-004: стадии открыты строго по роли из таблицы профиля — следователь и субъект без роли не пишут решений; фигурантов дела видят только роли, а чужой фигурант не виден никому")]
    public async Task Roles_from_profile_table_gate_stages_and_case_persons()
    {
        // Настоящая политика над investigation.user_role_assignment: одна роль — одна стадия.
        (await _policy.CanActAsync(VerificationStage.Expert, Expert)).ShouldBeTrue();
        (await _policy.CanActAsync(VerificationStage.Verifier, Expert)).ShouldBeFalse();
        (await _policy.CanActAsync(VerificationStage.Verifier, Verifier)).ShouldBeTrue();
        (await _policy.CanActAsync(VerificationStage.Verifier, SecondVerifier)).ShouldBeTrue();
        (await _policy.CanActAsync(VerificationStage.Expert, Verifier)).ShouldBeFalse();
        (await _policy.CanActAsync(VerificationStage.Expert, Investigator)).ShouldBeFalse();
        (await _policy.CanActAsync(VerificationStage.Verifier, Investigator)).ShouldBeFalse();
        (await _policy.CanActAsync(VerificationStage.Expert, NoRole)).ShouldBeFalse();
        (await _policy.CanActAsync(VerificationStage.Verifier, NoRole)).ShouldBeFalse();

        // Проверка «фигурант ДЕЛА» (ТФ-ВЕР-03) опирается на ICaseScope.ListPersonsAsync под решёткой:
        // эксперт и следователь дела видят фигуранта, субъект без роли — пусто (ТБ-012), чужого id нет ни у кого.
        (await _scope.ListPersonsAsync(_caseId, Access(Expert))).Select(p => p.PersonId).ShouldBe([_personId]);
        (await _scope.ListPersonsAsync(_caseId, Access(Investigator))).Select(p => p.PersonId).ShouldBe([_personId]);
        (await _scope.ListPersonsAsync(_caseId, Access(NoRole))).ShouldBeEmpty();
        (await _scope.ListPersonsAsync(_caseId, Access(Expert))).ShouldNotContain(p => p.PersonId == _personId + 1000);

        // Верификация до эксперта и стадия эксперта повторно — отклоняются правилом ещё до хранилища.
        TwoPersonRule.CanDecide([], VerificationStage.Verifier, Verifier, out var early).ShouldBeFalse();
        early.ShouldNotBeNull().ShouldContain("после решения эксперта");
    }

    [Fact(DisplayName = "GATE-5: два «подтверждён» разных сотрудников → «следственная версия» и появление фигуранта с обоими субъектами; до решения верификатора появления нет")]
    public async Task Two_confirmations_by_different_subjects_confirm_candidate_and_record_appearance()
    {
        var (sessionId, candidateId) = await CreateSessionAsync();

        // (1) Сразу после поиска — «кандидат», решений нет: ни поиск, ни чтение статус не назначают.
        var fresh = (await _sessions.GetCandidateAsync(candidateId, Access(Investigator))).ShouldNotBeNull();
        fresh.Status.ShouldBe(CandidateStatus.Candidate);
        fresh.Decisions.ShouldBeEmpty();
        fresh.PersonRef.ShouldBeNull();

        // (2) Эксперт 40 «подтверждён» с фигурантом дела → «ждёт верификатора», появления ещё нет.
        var expertDecision = await DecideAsync(Expert, candidateId, VerificationStage.Expert, VerificationVerdict.Confirmed, _personId);
        expertDecision.Status.ShouldBe(CandidateStatus.PendingVerifier);
        (await CountAppearancesAsync(candidateId)).ShouldBe(0);
        (await CountDecisionsAsync(candidateId)).ShouldBe(1);
        (await _sessions.ListQueueAsync(VerificationStage.Expert, [_caseId], Access(Expert))).ShouldBeEmpty();
        (await _sessions.ListQueueAsync(VerificationStage.Verifier, [_caseId], Access(Verifier))).ShouldHaveSingleItem().Id.ShouldBe(candidateId);

        // (3) Верификатор 41 «подтверждён» → «следственная версия»; появление с обоими субъектами (ТФ-ПЕР-02).
        var verifierDecision = await DecideAsync(Verifier, candidateId, VerificationStage.Verifier, VerificationVerdict.Confirmed);
        verifierDecision.Status.ShouldBe(CandidateStatus.Confirmed);

        var confirmed = (await _sessions.GetCandidateAsync(candidateId, Access(Investigator))).ShouldNotBeNull();
        confirmed.Status.ShouldBe(CandidateStatus.Confirmed);
        confirmed.PersonRef.ShouldBe(_personId);
        confirmed.Decisions.Select(d => (d.Stage, d.SubjectId)).ShouldBe([(VerificationStage.Expert, Expert), (VerificationStage.Verifier, Verifier)]);

        await RecordAppearanceAsync(sessionId, confirmed, Expert, Verifier);

        await using var db = _investigation.CreateDbContext();
        var appearance = await db.Appearances.AsNoTracking().SingleAsync(a => a.CandidateId == candidateId);
        appearance.PersonId.ShouldBe(_personId);
        appearance.CaseId.ShouldBe(_caseId);
        appearance.SearchSessionId.ShouldBe(sessionId);
        appearance.MediaAssetId.ShouldBe(_assetId);
        appearance.MediaFaceId.ShouldBe(_faceId);
        appearance.ExpertUserId.ShouldBe(Expert);
        appearance.VerifierUserId.ShouldBe(Verifier);
        appearance.Status.ShouldBe(AppearanceStatus.InvestigativeLead);
        appearance.Classification.ShouldBe(CaseClassification);
        appearance.DivisionId.ShouldBe(CaseDivision);

        // Появление идемпотентно по кандидату: повтор не плодит строк.
        await RecordAppearanceAsync(sessionId, confirmed, Expert, Verifier);
        (await CountAppearancesAsync(candidateId)).ShouldBe(1);
    }

    [Fact(DisplayName = "GATE-5: тот же сотрудник на обеих стадиях отклоняется правилом («ТБ-073»), а профиль не превращает самоподтверждение в появление; другой верификатор завершает цепочку")]
    public async Task Same_subject_on_both_stages_is_rejected_by_rule_and_by_profile_guard()
    {
        var (sessionId, candidateId) = await CreateSessionAsync();
        (await DecideAsync(Expert, candidateId, VerificationStage.Expert, VerificationVerdict.Confirmed, _personId))
            .Status.ShouldBe(CandidateStatus.PendingVerifier);

        // Правило модуля: 40 не может быть верификатором своего же экспертного решения — даже если бы политика
        // ролей профиля это разрешила (в тесте она не спрашивается намеренно).
        var pending = (await _sessions.GetCandidateAsync(candidateId, Access(Investigator))).ShouldNotBeNull();
        TwoPersonRule.CanDecide(pending.Decisions, VerificationStage.Verifier, Expert, out var reason).ShouldBeFalse();
        reason.ShouldNotBeNull().ShouldContain("ТБ-073");

        // Даже с проскочившим решением статус — «неопределённо», а профиль отказывает в появлении (страховка ТБ-073).
        var selfDecision = new VerificationDecision(Expert, VerificationStage.Verifier, VerificationVerdict.Confirmed, Rationale, DateTime.UtcNow);
        TwoPersonRule.Resolve([.. pending.Decisions, selfDecision]).ShouldBe(CandidateStatus.Undetermined);
        var guard = await Should.ThrowAsync<InvalidOperationException>(
            () => RecordAppearanceAsync(sessionId, pending, Expert, Expert));
        guard.Message.ShouldContain("ТБ-073");

        (await CountDecisionsAsync(candidateId)).ShouldBe(1);
        (await CountAppearancesAsync(candidateId)).ShouldBe(0);
        (await _sessions.GetCandidateAsync(candidateId, Access(Investigator))).ShouldNotBeNull().Status.ShouldBe(CandidateStatus.PendingVerifier);

        // Цепочка не сломана: ДРУГОЙ верификатор (43) завершает её штатно.
        (await DecideAsync(SecondVerifier, candidateId, VerificationStage.Verifier, VerificationVerdict.Confirmed))
            .Status.ShouldBe(CandidateStatus.Confirmed);
        var confirmed = (await _sessions.GetCandidateAsync(candidateId, Access(Investigator))).ShouldNotBeNull();
        await RecordAppearanceAsync(sessionId, confirmed, Expert, SecondVerifier);

        await using var db = _investigation.CreateDbContext();
        var appearance = await db.Appearances.AsNoTracking().SingleAsync(a => a.CandidateId == candidateId);
        appearance.ExpertUserId.ShouldBe(Expert);
        appearance.VerifierUserId.ShouldBe(SecondVerifier);
    }

    [Fact(DisplayName = "GATE-5: «подтверждён» + «отклонён» → «неопределённо» без появления; повтор стадии верификатора отклоняется базой без следа")]
    public async Task Disagreement_yields_undetermined_without_appearance()
    {
        var (_, candidateId) = await CreateSessionAsync();
        (await DecideAsync(Expert, candidateId, VerificationStage.Expert, VerificationVerdict.Confirmed, _personId))
            .Status.ShouldBe(CandidateStatus.PendingVerifier);

        var after = await DecideAsync(Verifier, candidateId, VerificationStage.Verifier, VerificationVerdict.Rejected);
        after.Status.ShouldBe(CandidateStatus.Undetermined);
        after.Decisions.Count.ShouldBe(2);
        after.PersonRef.ShouldBe(_personId); // привязка эксперта сохраняется для эскалации руководителю
        (await CountAppearancesAsync(candidateId)).ShouldBe(0);

        // Стадия верификатора закрыта — второй верификатор её не «перерешает»: правило отказывает, а если
        // обойти правило — уникальный индекс хранилища откатывает транзакцию целиком (ТБ-073).
        TwoPersonRule.CanDecide(after.Decisions, VerificationStage.Verifier, SecondVerifier, out var reason).ShouldBeFalse();
        reason.ShouldNotBeNull().ShouldContain("уже записано");
        var repeat = new VerificationDecision(SecondVerifier, VerificationStage.Verifier, VerificationVerdict.Confirmed, Rationale, DateTime.UtcNow);
        var conflict = await Should.ThrowAsync<InvalidOperationException>(
            () => _sessions.RecordDecisionAsync(candidateId, repeat, CandidateStatus.Confirmed, personRef: null));
        conflict.Message.ShouldContain("ТБ-073");

        (await CountDecisionsAsync(candidateId)).ShouldBe(2);
        (await _sessions.GetCandidateAsync(candidateId, Access(Investigator))).ShouldNotBeNull().Status.ShouldBe(CandidateStatus.Undetermined);
    }

    [Fact(DisplayName = "GATE-5: кандидат с баллом 0,99 без решений людей остаётся «кандидатом» — ни повторный поиск, ни чтение сессии, очереди и кандидата статус не назначают")]
    public async Task High_similarity_candidate_stays_candidate_without_human_decision()
    {
        var (firstSession, firstCandidate) = await CreateSessionAsync(cosineDistance: 0.01);

        var candidate = (await _sessions.GetCandidateAsync(firstCandidate, Access(Investigator))).ShouldNotBeNull();
        candidate.Similarity.ShouldBe(0.99, tolerance: 1e-9);
        candidate.Status.ShouldBe(CandidateStatus.Candidate);
        candidate.Decisions.ShouldBeEmpty();
        TwoPersonRule.Resolve(candidate.Decisions).ShouldBe(CandidateStatus.Candidate);

        // Повторный поиск по тому же лицу, чтение сессии, очереди эксперта и кандидата — только чтение.
        var (secondSession, secondCandidate) = await CreateSessionAsync(cosineDistance: 0.01);
        secondSession.ShouldNotBe(firstSession);
        (await _sessions.GetAsync(firstSession, Access(Investigator))).ShouldNotBeNull().CandidateCount.ShouldBe(1);
        (await _sessions.ListCandidatesAsync(firstSession, Access(Investigator))).ShouldHaveSingleItem().Status.ShouldBe(CandidateStatus.Candidate);
        (await _sessions.ListByCaseAsync(_caseId, Access(Investigator))).Count.ShouldBe(2);

        var queue = await _sessions.ListQueueAsync(VerificationStage.Expert, [_caseId], Access(Expert));
        queue.Select(c => c.Id).OrderBy(id => id).ShouldBe([firstCandidate, secondCandidate]);
        queue.ShouldAllBe(c => c.Status == CandidateStatus.Candidate);
        (await _sessions.ListQueueAsync(VerificationStage.Verifier, [_caseId], Access(Verifier))).ShouldBeEmpty();

        await using (var db = _media.CreateDbContext())
        {
            (await db.VerificationDecisions.CountAsync()).ShouldBe(0);
            (await db.SearchCandidates.AsNoTracking().Select(c => c.Status).ToListAsync())
                .ShouldAllBe(s => s == CandidateStatus.Candidate);
        }

        await using (var inv = _investigation.CreateDbContext())
        {
            (await inv.Appearances.AnyAsync()).ShouldBeFalse();
        }
    }

    // ---- обвязка ----

    private static AccessContext Access(int userId) => InvestigationTestKit.Access(userId, CaseClassification, CaseDivision);

    /// <summary>
    /// Решение стадии в том порядке, в каком его проводит обработчик: роль по политике профиля → правило двух
    /// лиц по уже записанным решениям → статус по правилу → атомарная запись в хранилище. Возвращает кандидата
    /// после записи. Появление создаётся отдельно (<see cref="RecordAppearanceAsync"/>) — как и в обработчике.
    /// </summary>
    private async Task<SearchCandidateRow> DecideAsync(
        int userId, int candidateId, VerificationStage stage, VerificationVerdict verdict, int? personRef = null)
    {
        (await _policy.CanActAsync(stage, userId)).ShouldBeTrue($"роль пользователя {userId} не даёт права на стадию {stage}");

        var candidate = (await _sessions.GetCandidateAsync(candidateId, Access(userId))).ShouldNotBeNull();
        TwoPersonRule.CanDecide(candidate.Decisions, stage, userId, out var reason).ShouldBeTrue(reason);

        if (personRef is { } requested)
        {
            // ТФ-ВЕР-03 «фигурант ДЕЛА»: только из дела кандидата, видимого субъекту.
            (await _scope.ListPersonsAsync(candidate.CaseId, Access(userId))).ShouldContain(p => p.PersonId == requested);
        }

        var decision = new VerificationDecision(userId, stage, verdict, Rationale, DateTime.UtcNow);
        var decisions = new List<VerificationDecision>(candidate.Decisions) { decision };
        await _sessions.RecordDecisionAsync(candidateId, decision, TwoPersonRule.Resolve(decisions), personRef);

        return (await _sessions.GetCandidateAsync(candidateId, Access(userId))).ShouldNotBeNull();
    }

    private Task RecordAppearanceAsync(int sessionId, SearchCandidateRow candidate, int expertUserId, int verifierUserId) =>
        _scope.RecordAppearanceAsync(new ConfirmedAppearance(
            candidate.CaseId, candidate.PersonRef ?? _personId, sessionId, candidate.Id, candidate.FaceId, candidate.AssetId,
            candidate.FrameIndex, candidate.FrameTimestampMs, candidate.Similarity,
            candidate.Classification, candidate.DivisionId,
            expertUserId, verifierUserId, DateTime.UtcNow));

    /// <summary>Сессия поиска по делу с единственным кандидатом — посеянным лицом; возвращает идентификаторы сессии и кандидата.</summary>
    private async Task<(int SessionId, int CandidateId)> CreateSessionAsync(double cosineDistance = 0.10)
    {
        var draft = new SearchSessionDraft(
            CaseId: _caseId,
            AuthorizationRef: "поручение № 5",
            Scope: SearchScopeKind.CurrentCase,
            CaseIds: [_caseId],
            ProbeSha256: new string('A', 64),
            ProbeFaceId: null,
            ProbeCropStoredFileName: null,
            TopK: 20,
            MaxCosineDistance: 0.6,
            DetectorVersion: "yunet-test",
            EmbedderVersion: "sface-test",
            HnswEfSearch: 200,
            Classification: CaseClassification,
            DivisionId: CaseDivision,
            RequestedByUserId: Investigator);
        var candidate = new FaceCandidate(_faceId, _assetId, FrameIndex: null, FrameTimestampMs: null, cosineDistance,
            CaseClassification, CaseDivision, "sface-test");

        var sessionId = await _sessions.CreateAsync(draft, [candidate]);
        var candidateId = (await _sessions.ListCandidatesAsync(sessionId, Access(Investigator))).Single().Id;
        return (sessionId, candidateId);
    }

    private async Task<int> CountDecisionsAsync(int candidateId)
    {
        await using var db = _media.CreateDbContext();
        return await db.VerificationDecisions.CountAsync(d => d.CandidateId == candidateId);
    }

    private async Task<int> CountAppearancesAsync(int candidateId)
    {
        await using var db = _investigation.CreateDbContext();
        return await db.Appearances.CountAsync(a => a.CandidateId == candidateId);
    }

    /// <summary>Носитель с одним пригодным лицом в подразделении дела и под его грифом (ТБ-070).</summary>
    private static async Task<(int AssetId, int FaceId)> SeedFaceAsync(MediaDbContext db)
    {
        var asset = new MediaAsset
        {
            Kind = MediaKind.Image,
            OriginalFileName = "g5.jpg",
            StoredFileName = Guid.NewGuid().ToString("N") + ".jpg",
            ContentType = "image/jpeg",
            ContentHash = Guid.NewGuid().ToString("N"),
            ByteSize = 1,
            Classification = CaseClassification,
            DivisionId = CaseDivision,
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
            CropStoredFileName = null,
            Classification = CaseClassification,
            DivisionId = CaseDivision,
        };
        db.Faces.Add(face);
        await db.SaveChangesAsync();
        return (asset.Id, face.Id);
    }
}
