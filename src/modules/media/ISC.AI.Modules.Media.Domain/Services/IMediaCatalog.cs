using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Domain.Model;

namespace ISC.AI.Modules.Media.Domain.Services;

/// <summary>Носитель для чтения (карточка, списки) — без байтов; байты отдаёт эндпоинт раздачи.</summary>
public sealed record MediaAssetRow(
    int Id,
    MediaKind Kind,
    string OriginalFileName,
    string StoredFileName,
    string ContentType,
    long ByteSize,
    long? DurationMs,
    string? Source,
    DateTimeOffset? CapturedAt,
    short Classification,
    int DivisionId,
    int? UploadedByUserId,
    MediaIndexStatus IndexStatus,
    string? IndexError,
    string? DetectorVersion,
    string? EmbedderVersion,
    DateTime? IndexedAt,
    DateTime CreatedAt,
    int FaceCount);

/// <summary>Лицо на носителе для чтения (рамки на фото, шкала лиц видео, вырезки).</summary>
public sealed record FaceRow(
    int Id,
    int AssetId,
    int? FrameIndex,
    long? FrameTimestampMs,
    float BoxX,
    float BoxY,
    float BoxWidth,
    float BoxHeight,
    float DetectionScore,
    float QualityScore,
    bool QualityAcceptable,
    string? QualityReason,
    string? CropStoredFileName,
    int? TrackId,
    short Classification,
    int DivisionId);

/// <summary>
/// Порт чтения пакета «Медиа» (ТФ-МЕД-03, ТФ-ПЛ-03). Каждый метод применяет floor ядра и политику
/// профиля на стороне БД (ТБ-020): недоступный носитель неотличим от несуществующего.
/// </summary>
public interface IMediaCatalog
{
    /// <summary>Носитель, если доступен субъекту.</summary>
    Task<MediaAssetRow?> GetAsync(int assetId, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>Носители по идентификаторам (только доступные), в порядке убывания даты загрузки.</summary>
    Task<IReadOnlyList<MediaAssetRow>> ListAsync(IReadOnlyCollection<int> assetIds, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>Лица носителя (по кадру и позиции), если носитель доступен.</summary>
    Task<IReadOnlyList<FaceRow>> ListFacesAsync(int assetId, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>Одно лицо, если доступно.</summary>
    Task<FaceRow?> GetFaceAsync(int faceId, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>Шаблон лица для поиска «этого человека в других материалах» (ТФ-ПЛ-03); <see langword="null"/> — нет/недоступен.</summary>
    Task<float[]?> GetTemplateAsync(int faceId, AccessContext access, CancellationToken cancellationToken = default);
}
