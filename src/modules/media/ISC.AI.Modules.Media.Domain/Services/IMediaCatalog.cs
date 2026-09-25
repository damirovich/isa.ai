using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Domain.Model;

namespace ISC.AI.Modules.Media.Domain.Services;

/// <summary>Носитель для чтения (карточка, списки) — без байтов; байты отдаёт эндпоинт раздачи.</summary>
/// <remarks>
/// <c>TranscriptStatus</c> — состояние расшифровки речи (ADR-0026): нужен списку носителей дела и карточке,
/// чтобы показать «расшифровывается / готово / ошибка» без отдельного запроса на каждый носитель. Параметр
/// последний и со значением по умолчанию — добавлен без поломки существующих вызовов.
/// Хвост ADR-0028 (тоже с умолчаниями): <c>FrameRate</c> — нативная частота кадров видео по пробе (шаг «±1 кадр»,
/// номер кадра; <see langword="null"/> — не видео или до пробы, интерфейс показывает шаг 40 мс с пометкой),
/// <c>FrameWidth</c>/<c>FrameHeight</c> — размер кадра после автоповорота, <c>SourceAssetId</c>/<c>SourceTimestampMs</c> —
/// происхождение снимка кадра (видео-источник и момент записи; у обычных загрузок <see langword="null"/>).
/// </remarks>
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
    int FaceCount,
    TranscriptStatus TranscriptStatus = TranscriptStatus.NotApplicable,
    double? FrameRate = null,
    int? FrameWidth = null,
    int? FrameHeight = null,
    int? SourceAssetId = null,
    long? SourceTimestampMs = null);

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

    /// <summary>
    /// Расшифровка носителя (ADR-0026), если носитель доступен; иначе <see langword="null"/> (неотличимо от
    /// несуществующего). Фрагменты несут гриф носителя и проходят ту же решётку на стороне БД.
    /// </summary>
    Task<MediaTranscript?> GetTranscriptAsync(int assetId, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>
    /// Поиск по словам в расшифровках перечисленных носителей (обычно — носителей дела). Совпадение —
    /// по подстроке без учёта регистра: у киргизского нет морфологического словаря в PostgreSQL, а слово с
    /// аффиксами («үйдө», «үйгө») должно находиться по основе («үй»). Решётка — на стороне БД.
    /// </summary>
    /// <param name="assetIds">Область поиска — носители, уже прошедшие сужение по делам субъекта.</param>
    /// <param name="text">Искомые слова (подстрока, не короче 2 символов).</param>
    /// <param name="limit">Предел выдачи.</param>
    /// <param name="access">Контекст доступа субъекта.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    Task<IReadOnlyList<TranscriptHit>> SearchTranscriptsAsync(
        IReadOnlyCollection<int> assetIds,
        string text,
        int limit,
        AccessContext access,
        CancellationToken cancellationToken = default);
}
