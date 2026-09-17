namespace ISC.AI.Modules.Media.Domain.Model;

/// <summary>
/// Черновик поисковой сессии (ТО-инф-12): всё, что известно о поиске ДО записи кандидатов. Вектор пробы
/// сюда не входит и в базу не пишется (ТБ-074) — только хеш; копия пробного изображения — в аудите под
/// решёткой (ТБ-072). Гриф/подразделение — дела, в контексте которого ведётся поиск (ТБ-070).
/// </summary>
public sealed record SearchSessionDraft(
    int CaseId,
    string AuthorizationRef,
    SearchScopeKind Scope,
    IReadOnlyList<int> CaseIds,
    string ProbeSha256,
    int? ProbeFaceId,
    string? ProbeCropStoredFileName,
    int TopK,
    double? MaxCosineDistance,
    string DetectorVersion,
    string EmbedderVersion,
    int HnswEfSearch,
    short Classification,
    int DivisionId,
    int? RequestedByUserId);

/// <summary>Поисковая сессия, как она хранится (ТО-инф-12, ТФ-ПЛ-07).</summary>
public sealed record SearchSessionRow(
    int Id,
    int CaseId,
    string AuthorizationRef,
    SearchScopeKind Scope,
    IReadOnlyList<int> CaseIds,
    string ProbeSha256,
    int? ProbeFaceId,
    string? ProbeCropStoredFileName,
    int TopK,
    double? MaxCosineDistance,
    string DetectorVersion,
    string EmbedderVersion,
    short Classification,
    int DivisionId,
    int? RequestedByUserId,
    DateTime CreatedAt,
    int CandidateCount);

/// <summary>
/// Кандидат поисковой сессии с решениями верификации (ТФ-ПЛ-02, ТФ-ВЕР-01/02). Полная строка — для
/// хранилища и руководителя; очередь верификатора получает ПРОЕКЦИЮ без чужих решений (слепота, ТФ-ВЕР-02).
/// </summary>
public sealed record SearchCandidateRow(
    int Id,
    int SessionId,
    int CaseId,
    int Rank,
    int FaceId,
    int AssetId,
    int? FrameIndex,
    long? FrameTimestampMs,
    double CosineDistance,
    string? CropStoredFileName,
    string ModelVersion,
    short Classification,
    int DivisionId,
    CandidateStatus Status,
    int? PersonRef,
    IReadOnlyList<VerificationDecision> Decisions)
{
    /// <summary>Косинусная схожесть (1 − расстояние) — показывается ТОЛЬКО с предупреждением о вероятностной природе (ТЭ-006).</summary>
    public double Similarity => 1 - CosineDistance;
}
