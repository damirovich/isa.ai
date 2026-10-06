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
/// Хвост ADR-0038: <c>FilmstripStoredFileName</c>/<c>FilmstripTileCount</c>/<c>FilmstripStepMs</c> — лента кадров видео
/// (картинка в категории <c>media-filmstrips</c>, число кадров и шаг между ними; до переиндексации — <see langword="null"/>),
/// <c>RecordedAt</c> — «встроенное» время начала записи из метаданных файла (ТФ-МЕД-11; не путать с подтверждённой
/// оператором датой съёмки <c>CapturedAt</c>).
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
    long? SourceTimestampMs = null,
    string? FilmstripStoredFileName = null,
    int? FilmstripTileCount = null,
    long? FilmstripStepMs = null,
    DateTimeOffset? RecordedAt = null);

/// <summary>Сведения о материале носителя для строки кандидата (ТФ-ПЛ-02): время съёмки, загрузки и источник.</summary>
/// <param name="AssetId">Носитель.</param>
/// <param name="CapturedAt">Время съёмки, если известно.</param>
/// <param name="UploadedAtUtc">Время загрузки (UTC).</param>
/// <param name="Source">Источник материала.</param>
public sealed record AssetMaterialInfo(int AssetId, DateTimeOffset? CapturedAt, DateTime UploadedAtUtc, string? Source);

/// <summary>Отрезок трека лица в видео (ТФ-ПЕР-02, ADR-0037): одно лицо на соседних кадрах выборки.</summary>
/// <param name="FaceId">Лицо, по которому спросили.</param>
/// <param name="TrackId">Номер трека в пределах носителя.</param>
/// <param name="StartMs">Момент первого кадра трека, мс.</param>
/// <param name="EndMs">Момент последнего кадра трека, мс.</param>
/// <param name="Frames">Сколько кадров выборки в треке.</param>
public sealed record FaceTrackSpan(int FaceId, int TrackId, long StartMs, long EndMs, int Frames);

/// <summary>Запрос отрезка трека: лицо и, на случай переиндексации, его носитель и момент кадра.</summary>
/// <param name="FaceId">Лицо (как его запомнило появление).</param>
/// <param name="AssetId">Носитель лица.</param>
/// <param name="FrameTimestampMs">Момент кадра лица, мс; у фото — <see langword="null"/>.</param>
public sealed record FaceTrackRequest(int FaceId, int AssetId, long? FrameTimestampMs);

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

    /// <summary>
    /// Время съёмки, загрузки и источник носителей (ТФ-ПЛ-02) — одним запросом на страницу кандидатов; под решёткой
    /// (ТБ-020/021): носитель выше допуска в ответ не попадает.
    /// </summary>
    Task<IReadOnlyDictionary<int, AssetMaterialInfo>> ListMaterialInfoAsync(
        IReadOnlyCollection<int> assetIds, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>
    /// Отрезки треков лиц из <paramref name="requests"/> (ТФ-ПЕР-02, ADR-0037): первый и последний кадр трека и число
    /// кадров — для показа появления фигуранта «с … по …»; ключ ответа — лицо из запроса. Лица без трека (фото, видео,
    /// проиндексированное до треков) в ответ не попадают; кадры трека считаются под решёткой (ТБ-020/021).
    /// </summary>
    /// <remarks>
    /// Переиндексация пересоздаёт лица с новыми номерами, а появление помнит прежний. Если прежнего лица нет, трек
    /// берётся у лица того же носителя на том же кадре — ТОЛЬКО если оно на этом кадре одно: при нескольких лицах
    /// угадывать нельзя, и отрезок не показывается.
    /// </remarks>
    Task<IReadOnlyDictionary<int, FaceTrackSpan>> GetTrackSpansAsync(
        IReadOnlyCollection<FaceTrackRequest> requests, AccessContext access, CancellationToken cancellationToken = default);

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

    /// <summary>
    /// Копии тех же файлов по содержимому (одинаковый SHA-256) — для пересечения «объект на материале другого дела»
    /// (ТФ-ПЕР-07, ADR-0029 п. 3а). Один файл, загруженный в дела с разным грифом или подразделением, хранится
    /// отдельными носителями (ключ дедупликации — подразделение, гриф, хеш; ТБ-070), и без этого метода такое
    /// совпадение терялось бы.
    /// </summary>
    /// <remarks>
    /// ИНВАРИАНТ (ТБ-020/021): и исходный носитель, и копия проходят floor ядра и политику профиля на стороне БД;
    /// копия выше допуска субъекта не возвращается и ничем не выдаёт своего существования. Хеш наружу не отдаётся.
    /// </remarks>
    /// <param name="assetIds">Исходные носители.</param>
    /// <param name="access">Контекст доступа субъекта.</param>
    /// <param name="cancellationToken">Токен отмены.</param>
    /// <returns>Пары «исходный носитель → другой носитель с тем же содержимым»; сам исходный в копии не входит.</returns>
    Task<IReadOnlyList<MediaContentTwin>> ListContentTwinsAsync(
        IReadOnlyCollection<int> assetIds,
        AccessContext access,
        CancellationToken cancellationToken = default);
}

/// <summary>Другой носитель с тем же содержимым файла, что и исходный (см. <see cref="IMediaCatalog.ListContentTwinsAsync"/>).</summary>
/// <param name="AssetId">Исходный носитель.</param>
/// <param name="TwinAssetId">Носитель-копия (тот же SHA-256, другой гриф или подразделение).</param>
public sealed record MediaContentTwin(int AssetId, int TwinAssetId);
