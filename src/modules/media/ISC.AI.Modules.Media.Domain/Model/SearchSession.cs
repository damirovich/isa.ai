namespace ISC.AI.Modules.Media.Domain.Model;

/// <summary>
/// Черновик поисковой сессии (ТО-инф-12): всё, что известно о поиске ДО записи кандидатов. Вектор пробы
/// сюда не входит и в базу не пишется (ТБ-074) — только хеш; копия пробного изображения — в аудите под
/// решёткой (ТБ-072). Гриф/подразделение — дела, в контексте которого ведётся поиск (ТБ-070).
/// </summary>
/// <remarks>
/// <c>Origin</c> — кто запустил: сотрудник или система (ТФ-ПЕР-09). <c>SuggestedPersonRef</c> — фигурант дела, чей
/// эталон был пробой автоматического предложения (непрозрачный идентификатор профиля); это подсказка эксперту,
/// а не решение: привязку к фигуранту (<c>PersonRef</c> кандидата) по-прежнему ставит эксперт (ТФ-ВЕР-03).
/// <c>ProbeFaceIds</c> — поиск по нескольким эталонам (ADR-0046): все пригодные лица-эталоны, первое совпадает с
/// <c>ProbeFaceId</c>; у одиночного поиска — <see langword="null"/>.
/// </remarks>
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
    int? RequestedByUserId,
    SessionOrigin Origin = SessionOrigin.Operator,
    int? SuggestedPersonRef = null,
    IReadOnlyList<int>? ProbeFaceIds = null);

/// <summary>
/// Поисковая сессия, как она хранится (ТО-инф-12, ТФ-ПЛ-07). <c>ProbeFaceIds</c> — эталоны поиска по нескольким
/// эталонам (ADR-0046); у одиночного поиска — <see langword="null"/>.
/// </summary>
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
    int CandidateCount,
    SessionOrigin Origin = SessionOrigin.Operator,
    int? SuggestedPersonRef = null,
    IReadOnlyList<int>? ProbeFaceIds = null)
{
    /// <summary>Поиск по нескольким эталонам (ADR-0046).</summary>
    public bool IsMultiProbe => ProbeFaceIds is { Count: > 1 };
}

/// <summary>
/// Кандидат поисковой сессии с решениями верификации (ТФ-ПЛ-02, ТФ-ВЕР-01/02). Полная строка — для
/// хранилища и руководителя; очередь верификатора получает ПРОЕКЦИЮ без чужих решений (слепота, ТФ-ВЕР-02).
/// <c>ProbeFaceId</c> и <c>ProbeMatchCount</c> — поиск по нескольким эталонам (ADR-0046): эталон лучшего совпадения
/// (он и показывается в паре «проба ↔ кандидат», ТФ-ВЕР-01) и число совпавших эталонов; у одиночного поиска — пусто.
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
    IReadOnlyList<VerificationDecision> Decisions,
    float? QualityScore = null,
    SessionOrigin Origin = SessionOrigin.Operator,
    int? SuggestedPersonRef = null,
    int? ProbeFaceId = null,
    int? ProbeMatchCount = null)
{
    /// <summary>Косинусная схожесть (1 − расстояние) — показывается ТОЛЬКО с предупреждением о вероятностной природе (ТЭ-006).</summary>
    public double Similarity => 1 - CosineDistance;
}
