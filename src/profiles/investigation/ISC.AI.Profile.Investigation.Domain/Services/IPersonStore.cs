using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Domain.Enums;

namespace ISC.AI.Profile.Investigation.Domain.Services;

/// <summary>Черновик фигуранта (ТФ-ПЕР-01). Пустое имя при <paramref name="IsUnidentified"/> → «Неустановленное лицо № N».</summary>
public sealed record PersonDraft(int CaseId, string? DisplayName, bool IsUnidentified, string? RoleInCase, string? Notes);

/// <summary>Фигурант в списке/карточке.</summary>
public sealed record PersonRow(
    int Id,
    int CaseId,
    string DisplayName,
    bool IsUnidentified,
    int? UnidentifiedNumber,
    string? RoleInCase,
    string? Notes,
    short Classification,
    int DivisionId,
    int ReferencePhotoCount,
    int AppearanceCount);

/// <summary>Подтверждённое появление (ТФ-ПЕР-02).</summary>
public sealed record AppearanceRow(
    int Id,
    int PersonId,
    int CaseId,
    int MediaAssetId,
    int MediaFaceId,
    int? FrameIndex,
    long? FrameTimestampMs,
    int SearchSessionId,
    int CandidateId,
    double Similarity,
    AppearanceStatus Status,
    DateTime ConfirmedAtUtc,
    int ExpertUserId,
    int VerifierUserId);

/// <summary>Черновик появления — из подтверждённого кандидата модуля «Медиа».</summary>
public sealed record AppearanceDraft(
    int PersonId,
    int CaseId,
    int MediaAssetId,
    int MediaFaceId,
    int? FrameIndex,
    long? FrameTimestampMs,
    int SearchSessionId,
    int CandidateId,
    double Similarity,
    DateTime ConfirmedAtUtc,
    int ExpertUserId,
    int VerifierUserId,
    short Classification,
    int DivisionId);

/// <summary>Эталон фигуранта (ТФ-ПЕР-01, ТБ-077).</summary>
public sealed record ReferencePhotoRow(
    int Id, int PersonId, int MediaAssetId, int? MediaFaceId, float? QualityScore, string? Source,
    string? LegalBasis, DateOnly? ReviewDueAt, int? AddedByUserId, int? SupersededById, DateTime CreatedAt);

/// <summary>Черновик эталона.</summary>
public sealed record ReferencePhotoDraft(
    int PersonId, int MediaAssetId, int? MediaFaceId, float? QualityScore, string? Source,
    string? LegalBasis, DateOnly? ReviewDueAt, int? AddedByUserId);

/// <summary>Исход записи фигуранта.</summary>
public enum PersonWriteResult
{
    /// <summary>Успех.</summary>
    Ok = 0,

    /// <summary>Фигурант/дело не найдены или недоступны.</summary>
    NotFound = 1,
}

/// <summary>Хранилище фигурантов, эталонов и появлений (ТФ-ПЕР-01/02). Чтение — под решёткой; гриф/подразделение берутся у дела.</summary>
public interface IPersonStore
{
    /// <summary>Фигуранты дела.</summary>
    Task<IReadOnlyList<PersonRow>> ListByCaseAsync(int caseId, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>Фигурант, если доступен.</summary>
    Task<PersonRow?> GetAsync(int personId, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>Создать фигуранта в деле (дело должно быть доступно).</summary>
    Task<(PersonWriteResult Result, int PersonId)> CreateAsync(PersonDraft draft, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>Изменить реквизиты.</summary>
    Task<PersonWriteResult> UpdateAsync(int personId, string? displayName, bool isUnidentified, string? roleInCase, string? notes, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>Появления фигуранта (только подтверждённые, ТБ-073), новые первыми.</summary>
    Task<IReadOnlyList<AppearanceRow>> ListAppearancesAsync(int personId, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>Записать появление (вызывается модулем «Медиа» через порт после подтверждения; без решётки — факт уже проверен).</summary>
    Task<int> AddAppearanceAsync(AppearanceDraft draft, CancellationToken cancellationToken = default);

    /// <summary>Эталоны фигуранта.</summary>
    Task<IReadOnlyList<ReferencePhotoRow>> ListReferencePhotosAsync(int personId, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>Добавить эталон; прежний актуальный (если <paramref name="supersedesId"/>) помечается заменённым, но не удаляется (ТБ-077).</summary>
    Task<(PersonWriteResult Result, int PhotoId)> AddReferencePhotoAsync(ReferencePhotoDraft draft, int? supersedesId, AccessContext access, CancellationToken cancellationToken = default);
}
