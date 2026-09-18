namespace ISC.AI.Modules.Media.Domain.Model;

/// <summary>
/// Запрос поиска «лицо по фото» (ТС-012, режим identification ISO/IEC 19795): вектор-проба
/// (L2-нормированный, размерность модели) и ширина выдачи. Контекст доступа передаётся ОТДЕЛЬНЫМ
/// обязательным параметром порта — его отсутствие есть отказ, а не «поиск без фильтра» (ТБ-021).
/// </summary>
/// <param name="Probe">Вектор пробы (выход <see cref="Services.IFaceEmbedder"/>).</param>
/// <param name="TopK">Сколько кандидатов вернуть (rank-k, ТО-мат-05).</param>
/// <param name="MaxCosineDistance">
/// Порог отсечения по косинусному расстоянию (1 − cos): кандидаты дальше порога не выдаются.
/// <see langword="null"/> — без отсечения (только ранжирование). Порог задаёт эксплуатант (ТО-мат-05).
/// </param>
/// <param name="IncludeStale">Включать шаблоны неактуальных носителей (по умолчанию — нет, ADR-0013).</param>
/// <param name="AssetIds">
/// Область поиска — носители дел, доступных субъекту (ТФ-ПЛ-05, ТБ-071); применяется ПОСЛЕ решётки, а не
/// вместо неё. <see langword="null"/> — все доступные по решётке носители (только для служебных сценариев).
/// </param>
public sealed record FaceSearchQuery(
    float[] Probe,
    int TopK = 20,
    double? MaxCosineDistance = null,
    bool IncludeStale = false,
    IReadOnlyCollection<int>? AssetIds = null);

/// <summary>
/// Кандидат выдачи поиска по лицу (ТС-012): какое лицо, на каком носителе/кадре, насколько близко.
/// Режимные поля возвращаются вместе с кандидатом: вызывающий обязан показывать гриф (ТБ-073).
/// </summary>
/// <param name="FaceId">Лицо (media.face).</param>
/// <param name="AssetId">Носитель (media.asset).</param>
/// <param name="FrameIndex">Индекс кадра для видео; <see langword="null"/> — изображение.</param>
/// <param name="FrameTimestampMs">Таймкод кадра, мс; <see langword="null"/> — изображение.</param>
/// <param name="CosineDistance">Косинусное расстояние (1 − cos); меньше — ближе.</param>
/// <param name="Classification">Гриф носителя (денормализован на шаблон, ТБ-020).</param>
/// <param name="DivisionId">Подразделение-владелец.</param>
/// <param name="ModelVersion">Версия модели, породившей шаблон (ТО-прог-11).</param>
public sealed record FaceCandidate(
    int FaceId,
    int AssetId,
    int? FrameIndex,
    long? FrameTimestampMs,
    double CosineDistance,
    short Classification,
    int DivisionId,
    string ModelVersion)
{
    /// <summary>Косинусная схожесть (cos = 1 − расстояние) — для отображения и порогов ТО-мат-05.</summary>
    public double Similarity => 1 - CosineDistance;
}

/// <summary>
/// Описание нового носителя для приёма в хранилище (ТС-010, ТП-004). Гриф и подразделение
/// ОБЯЗАТЕЛЬНЫ на входе — носитель без режимных меток не принимается (ТБ-020).
/// </summary>
/// <param name="OriginalFileName">Исходное имя файла (для отображения; на диске — GUID).</param>
/// <param name="ContentType">
/// MIME-тип из allowlist пакета. Заявляется клиентом; сценарий приёма сверяет семейство (изображение/видео)
/// с сигнатурой содержимого и отклоняет подделку, точный тип по содержимому окончательно устанавливает
/// декодер конвейера (нераспознанное → носитель в статусе «ошибка», не в поиске).
/// </param>
/// <param name="Kind">Вид носителя.</param>
/// <param name="Classification">Гриф носителя.</param>
/// <param name="DivisionId">Подразделение-владелец.</param>
/// <param name="Source">Источник/происхождение (откуда получена запись — ТФ-МЕД, свободный текст).</param>
/// <param name="CapturedAt">Дата/время съёмки, если известны.</param>
/// <param name="UploadedByUserId">Кто загрузил (слабая ссылка на <c>core.app_user</c>).</param>
public sealed record MediaAssetDraft(
    string OriginalFileName,
    string ContentType,
    MediaKind Kind,
    short Classification,
    int DivisionId,
    string? Source = null,
    DateTimeOffset? CapturedAt = null,
    int? UploadedByUserId = null);

/// <summary>Результат приёма носителя: идентификатор и признак «такой файл уже был» (дедуп по хешу).</summary>
/// <param name="AssetId">Носитель в схеме <c>media</c>.</param>
/// <param name="Duplicate">
/// <see langword="true"/> — файл с тем же SHA-256 уже хранится в том же подразделении; новая
/// запись не создана, возвращён существующий носитель.
/// </param>
public sealed record MediaAssetReceipt(int AssetId, bool Duplicate);

/// <summary>
/// Лицо, найденное конвейером, вместе с шаблоном — единица записи результата индексации (ТП-005).
/// </summary>
/// <param name="FrameIndex">Индекс кадра (видео) либо <see langword="null"/> (изображение).</param>
/// <param name="FrameTimestampMs">Таймкод кадра, мс (видео).</param>
/// <param name="Face">Рамка, точки и балл детектора.</param>
/// <param name="Quality">Оценка пригодности (шаблон пишется и для непригодных — с флагом).</param>
/// <param name="Template">L2-нормированный вектор; <see langword="null"/> — шаблон не строился.</param>
/// <param name="CropStoredFileName">Имя файла вырезки лица в хранилище (для показа в выдаче), если сохранена.</param>
/// <param name="TrackId">Идентификатор трека лица в видео (одно лицо на серии кадров), если известен.</param>
public sealed record IndexedFace(
    int? FrameIndex,
    long? FrameTimestampMs,
    DetectedFace Face,
    FaceQuality Quality,
    float[]? Template,
    string? CropStoredFileName = null,
    int? TrackId = null);

/// <summary>Результат гарантированного удаления носителя (ТБ-064 для медиа, GATE-6).</summary>
/// <param name="Found">Носитель существовал (иначе — идемпотентный no-op без аудита).</param>
/// <param name="FacesRemoved">Сколько строк лиц снято (для отчёта оператору).</param>
/// <param name="FilesRemoved">Сколько файлов (исходник + вырезки) удалено из хранилища.</param>
public sealed record MediaPurgeResult(bool Found, int FacesRemoved, int FilesRemoved)
{
    /// <summary>Носитель не найден — удалять нечего.</summary>
    public static MediaPurgeResult NotFound { get; } = new(false, 0, 0);
}

/// <summary>
/// Результат снятия биометрического слоя с носителей (ТБ-074, ТФ-ДЕЛ-04): что именно удалено.
/// Числа попадают в акт об удалении шаблонов, поэтому это не отладочная сводка, а документируемый факт.
/// </summary>
/// <param name="AssetsAffected">Сколько носителей затронуто (у которых был хотя бы один шаблон или вырезка).</param>
/// <param name="TemplatesRemoved">Сколько шаблонов лиц снято — ровно столько векторов ушло из индекса поиска.</param>
/// <param name="CropsRemoved">Сколько файлов вырезок удалено из хранилища.</param>
public sealed record TemplatePurgeResult(int AssetsAffected, int TemplatesRemoved, int CropsRemoved)
{
    /// <summary>Удалять было нечего (шаблонов нет или список носителей пуст) — аудит не пишется.</summary>
    public static TemplatePurgeResult Empty { get; } = new(0, 0, 0);
}

/// <summary>Метаданные носителя для конвейера индексации (фон, без субъекта — решётка здесь не применяется).</summary>
/// <param name="AssetId">Носитель.</param>
/// <param name="Kind">Вид.</param>
/// <param name="StoredFileName">Имя исходника в хранилище (категория <c>media-originals</c>).</param>
/// <param name="ContentType">MIME.</param>
/// <param name="Classification">Гриф носителя — наследуется лицами и шаблонами (ТБ-070).</param>
/// <param name="DivisionId">Подразделение.</param>
/// <param name="ExistingCropFileNames">Имена вырезок прежней индексации — удалить с диска перед перезаписью.</param>
public sealed record MediaAssetIndexingInfo(
    int AssetId,
    MediaKind Kind,
    string StoredFileName,
    string ContentType,
    short Classification,
    int DivisionId,
    IReadOnlyList<string> ExistingCropFileNames);
