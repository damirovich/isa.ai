using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Data.Entities;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Modules.Media.Data;

/// <summary>
/// Хранилище поисковых сессий, кандидат-листов и решений верификации в схеме <c>media</c>
/// (ТО-инф-12, ТФ-ПЛ-07, ТФ-ВЕР-01/02). Чтение — под решёткой ядра и политикой профиля НА СТОРОНЕ БД
/// и для сессии, и для кандидата (обе сущности режимные, ТБ-020/070): кандидат виден, только если видна
/// его сессия (гриф дела) И сам кандидат (гриф носителя-источника). Запись решения и статуса — одной
/// транзакцией (ТБ-073); иных путей изменить статус кандидата в хранилище нет.
/// </summary>
/// <remarks>
/// Слепота верификатора (ТФ-ВЕР-02) — ответственность сценария приложения: хранилище отдаёт полные строки
/// с решениями, а проекцию «без чужих решений» строит обработчик очереди. Проба (вектор) сюда не попадает
/// никогда (ТБ-074): в сессии — только хеш и имя вырезки.
/// </remarks>
/// <param name="contextFactory">Фабрика контекста (на операцию, ТС-008).</param>
/// <param name="accessPolicy">Сужающая политика профиля (ADR-0014).</param>
public sealed class SearchSessionStore(
    IDbContextFactory<MediaDbContext> contextFactory,
    IAccessPolicy accessPolicy) : ISearchSessionStore
{
    /// <inheritdoc />
    public async Task<int> CreateAsync(
        SearchSessionDraft draft, IReadOnlyList<FaceCandidate> candidates, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentNullException.ThrowIfNull(candidates);

        // ТБ-024/070: без подразделения сессия не создаётся (гриф 0 допустим — «открыто»); ТБ-071: без
        // основания поиск не ведётся. Проверка валидатора дублируется здесь как последний рубеж.
        if (draft.DivisionId <= 0)
        {
            throw new ArgumentException("Поисковая сессия без подразделения не создаётся (ТБ-024/070).", nameof(draft));
        }

        if (string.IsNullOrWhiteSpace(draft.AuthorizationRef))
        {
            throw new ArgumentException("Поиск без основания не ведётся (ТБ-071).", nameof(draft));
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Имя вырезки лица берётся у лица базы на момент поиска: потом носитель могут удалить, а история
        // кандидата с именем файла (уже недоступного) останется (ТБ-072).
        var faceIds = candidates.Select(c => c.FaceId).Distinct().ToArray();
        var crops = faceIds.Length == 0
            ? new Dictionary<int, string?>()
            : await db.Faces.AsNoTracking()
                .Where(f => faceIds.Contains(f.Id))
                .Select(f => new { f.Id, f.CropStoredFileName })
                .ToDictionaryAsync(f => f.Id, f => f.CropStoredFileName, cancellationToken);

        // ОДНА транзакция: сессия и весь кандидат-лист либо записаны целиком, либо нет (ТБ-072).
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var session = new SearchSession
        {
            CaseId = draft.CaseId,
            AuthorizationRef = draft.AuthorizationRef,
            Scope = draft.Scope,
            CaseIds = draft.CaseIds.ToArray(),
            ProbeSha256 = draft.ProbeSha256,
            ProbeFaceId = draft.ProbeFaceId,
            ProbeCropStoredFileName = draft.ProbeCropStoredFileName,
            TopK = draft.TopK,
            MaxCosineDistance = draft.MaxCosineDistance,
            DetectorVersion = draft.DetectorVersion,
            EmbedderVersion = draft.EmbedderVersion,
            HnswEfSearch = draft.HnswEfSearch,
            Classification = draft.Classification,
            DivisionId = draft.DivisionId,
            RequestedByUserId = draft.RequestedByUserId,
        };
        db.SearchSessions.Add(session);

        for (var i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            db.SearchCandidates.Add(new SearchCandidate
            {
                Session = session,
                Rank = i + 1,
                FaceId = candidate.FaceId,
                AssetId = candidate.AssetId,
                FrameIndex = candidate.FrameIndex,
                FrameTimestampMs = candidate.FrameTimestampMs,
                CosineDistance = candidate.CosineDistance,
                CropStoredFileName = crops.GetValueOrDefault(candidate.FaceId),
                ModelVersion = candidate.ModelVersion,
                // Режимные поля — носителя-источника, как их выдал поиск (ТБ-070).
                Classification = candidate.Classification,
                DivisionId = candidate.DivisionId,
                Status = CandidateStatus.Candidate,
            });
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return session.Id;
    }

    /// <inheritdoc />
    public async Task<SearchSessionRow?> GetAsync(int sessionId, AccessContext access, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var row = await ProjectSessions(db, VisibleSessions(db, access).Where(s => s.Id == sessionId))
            .FirstOrDefaultAsync(cancellationToken);
        return row is null ? null : ToRow(row);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SearchSessionRow>> ListByCaseAsync(int caseId, AccessContext access, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        // Сортировка ДО проекции: после неё EF не транслирует порядок по строке с массивом дел.
        var rows = await ProjectSessions(
                db,
                VisibleSessions(db, access).Where(s => s.CaseId == caseId)
                    .OrderByDescending(s => s.CreatedAt).ThenByDescending(s => s.Id))
            .ToListAsync(cancellationToken);
        return rows.Select(ToRow).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SearchCandidateRow>> ListCandidatesAsync(int sessionId, AccessContext access, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await MaterializeAsync(
            db,
            VisibleCandidates(db, access).Where(c => c.SessionId == sessionId).OrderBy(c => c.Rank),
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<SearchCandidateRow?> GetCandidateAsync(int candidateId, AccessContext access, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await MaterializeAsync(
            db, VisibleCandidates(db, access).Where(c => c.Id == candidateId), cancellationToken);
        return rows.Count == 0 ? null : rows[0];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SearchCandidateRow>> ListQueueAsync(
        VerificationStage stage, IReadOnlyCollection<int> caseIds, AccessContext access, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caseIds);
        if (caseIds.Count == 0)
        {
            return []; // область дел пуста — очередь пуста, а не «все дела» (ТБ-071)
        }

        var status = stage switch
        {
            VerificationStage.Expert => CandidateStatus.Candidate,
            VerificationStage.Verifier => CandidateStatus.PendingVerifier,
            _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "Неизвестная стадия верификации."),
        };
        var ids = caseIds as int[] ?? caseIds.ToArray();

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await MaterializeAsync(
            db,
            VisibleCandidates(db, access)
                .Where(c => c.Status == status && ids.Contains(c.Session!.CaseId))
                .OrderBy(c => c.SessionId).ThenBy(c => c.Rank),
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task RecordDecisionAsync(
        int candidateId, VerificationDecision decision, CandidateStatus newStatus, int? personRef, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decision);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        // ОДНА транзакция (ТБ-073): решение и статус фиксируются вместе — нет состояния «решение есть,
        // статус старый» и наоборот. Повтор стадии упирается в уникальный индекс (candidate_id, stage).
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var target = db.SearchCandidates.Where(c => c.Id == candidateId);
        var updated = personRef is { } person
            ? await target.ExecuteUpdateAsync(
                s => s.SetProperty(c => c.Status, newStatus).SetProperty(c => c.PersonRef, person), cancellationToken)
            : await target.ExecuteUpdateAsync(
                s => s.SetProperty(c => c.Status, newStatus), cancellationToken);
        if (updated == 0)
        {
            throw new InvalidOperationException($"Кандидат {candidateId} не найден — решение не записано.");
        }

        db.VerificationDecisions.Add(new VerificationDecisionEntity
        {
            CandidateId = candidateId,
            SubjectId = decision.SubjectId,
            Stage = decision.Stage,
            Verdict = decision.Verdict,
            Rationale = decision.Rationale,
            DecidedAtUtc = AsUtc(decision.DecidedAtUtc),
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    // --- решётка: сессия и кандидат — обе режимные сущности (ТБ-020/070) ---

    private IQueryable<SearchSession> VisibleSessions(MediaDbContext db, AccessContext access) =>
        db.SearchSessions.AsNoTracking().VisibleTo(access, accessPolicy);

    private IQueryable<SearchCandidate> VisibleCandidates(MediaDbContext db, AccessContext access)
    {
        var sessions = VisibleSessions(db, access);
        return db.SearchCandidates.AsNoTracking()
            .VisibleTo(access, accessPolicy)
            .Where(c => sessions.Any(s => s.Id == c.SessionId));
    }

    // --- проекции ---

    private sealed record SessionProjection(
        int Id, int CaseId, string AuthorizationRef, SearchScopeKind Scope, int[] CaseIds, string ProbeSha256,
        int? ProbeFaceId, string? ProbeCropStoredFileName, int TopK, double? MaxCosineDistance, string DetectorVersion,
        string EmbedderVersion, short Classification, int DivisionId, int? RequestedByUserId, DateTime CreatedAt, int CandidateCount);

    private static IQueryable<SessionProjection> ProjectSessions(MediaDbContext db, IQueryable<SearchSession> sessions) =>
        sessions.Select(s => new SessionProjection(
            s.Id, s.CaseId, s.AuthorizationRef, s.Scope, s.CaseIds, s.ProbeSha256, s.ProbeFaceId, s.ProbeCropStoredFileName,
            s.TopK, s.MaxCosineDistance, s.DetectorVersion, s.EmbedderVersion, s.Classification, s.DivisionId,
            s.RequestedByUserId, s.CreatedAt, db.SearchCandidates.Count(c => c.SessionId == s.Id)));

    private static SearchSessionRow ToRow(SessionProjection s) => new(
        s.Id, s.CaseId, s.AuthorizationRef, s.Scope, s.CaseIds, s.ProbeSha256, s.ProbeFaceId, s.ProbeCropStoredFileName,
        s.TopK, s.MaxCosineDistance, s.DetectorVersion, s.EmbedderVersion, s.Classification, s.DivisionId,
        s.RequestedByUserId, s.CreatedAt, s.CandidateCount);

    private sealed record CandidateProjection(
        int Id, int SessionId, int CaseId, int Rank, int FaceId, int AssetId, int? FrameIndex, long? FrameTimestampMs,
        double CosineDistance, string? CropStoredFileName, string ModelVersion, short Classification, int DivisionId,
        CandidateStatus Status, int? PersonRef);

    private static async Task<List<SearchCandidateRow>> MaterializeAsync(
        MediaDbContext db, IQueryable<SearchCandidate> candidates, CancellationToken cancellationToken)
    {
        var rows = await candidates
            .Select(c => new CandidateProjection(
                c.Id, c.SessionId, c.Session!.CaseId, c.Rank, c.FaceId, c.AssetId, c.FrameIndex, c.FrameTimestampMs,
                c.CosineDistance, c.CropStoredFileName, c.ModelVersion, c.Classification, c.DivisionId, c.Status, c.PersonRef))
            .ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            return [];
        }

        var ids = rows.Select(r => r.Id).ToArray();
        var decisions = await db.VerificationDecisions.AsNoTracking()
            .Where(d => ids.Contains(d.CandidateId))
            .OrderBy(d => d.Stage).ThenBy(d => d.Id)
            .ToListAsync(cancellationToken);
        var byCandidate = decisions.ToLookup(d => d.CandidateId);

        return rows.Select(r => new SearchCandidateRow(
            r.Id, r.SessionId, r.CaseId, r.Rank, r.FaceId, r.AssetId, r.FrameIndex, r.FrameTimestampMs, r.CosineDistance,
            r.CropStoredFileName, r.ModelVersion, r.Classification, r.DivisionId, r.Status, r.PersonRef,
            byCandidate[r.Id]
                .Select(d => new VerificationDecision(d.SubjectId, d.Stage, d.Verdict, d.Rationale, d.DecidedAtUtc))
                .ToList()))
            .ToList();
    }

    // Npgsql пишет timestamptz только из Kind=Utc; вход нормализуем, не доверяя вызывающему.
    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
