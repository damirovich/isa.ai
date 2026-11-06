using ISC.AI.Modules.Media.Domain.Model;

namespace ISC.AI.Modules.Media.Domain.Services;

/// <summary>
/// Носители для пакета обмена между офисами (ТФ-ОБМ-01, ADR-0068): реквизиты, лица по номерам и решения верификации по
/// кандидатам; файл оригинала — потоком. Шаблонов и векторов лиц порт не отдаёт (ТБ-076): получатель считает их сам.
/// </summary>
/// <remarks>
/// ИНВАРИАНТ (ТБ-079, ТБ-083): решётка допуска здесь НЕ применяется — пакет переносит данные офиса, а не то, что видит
/// сотрудник, и формирует его система по отбору метаданными. Поэтому вызывать порт вправе только сборщик пакета обмена
/// под правом «Обмен между офисами»: всё, что он прочитал, уходит зашифрованным для экземпляра-получателя и человеку не
/// показывается.
/// </remarks>
public interface IMediaExchangeSource
{
    /// <summary>Реквизиты носителей по номерам (неизвестные номера пропускаются).</summary>
    Task<IReadOnlyList<MediaExchangeAsset>> GetAssetsAsync(IReadOnlyCollection<int> assetIds, CancellationToken cancellationToken = default);

    /// <summary>Лица по номерам: кадр, таймкод и рамка — по ним получатель найдёт то же лицо в своей индексации.</summary>
    Task<IReadOnlyList<MediaExchangeFace>> GetFacesAsync(IReadOnlyCollection<int> faceIds, CancellationToken cancellationToken = default);

    /// <summary>Решения верификации по кандидатам (все этапы), по времени решения.</summary>
    Task<IReadOnlyList<MediaExchangeDecision>> GetDecisionsAsync(
        IReadOnlyCollection<int> candidateIds, CancellationToken cancellationToken = default);

    /// <summary>Файл оригинала носителя.</summary>
    /// <exception cref="FileNotFoundException">Носителя или его файла нет.</exception>
    Task<Stream> OpenOriginalAsync(int assetId, CancellationToken cancellationToken = default);
}

/// <summary>Реквизиты носителя для пакета обмена (без производных: индексации, расшифровки, ленты кадров, волны).</summary>
/// <param name="AssetId">Номер носителя в источнике.</param>
/// <param name="Kind">Вид носителя.</param>
/// <param name="OriginalFileName">Имя файла при загрузке.</param>
/// <param name="ContentType">MIME-тип.</param>
/// <param name="ContentHash">SHA-256 содержимого (прописные шестнадцатеричные).</param>
/// <param name="ByteSize">Объём файла.</param>
/// <param name="DurationMs">Длительность видео и звука.</param>
/// <param name="Source">Источник материала (реквизит).</param>
/// <param name="CapturedAt">Дата съёмки, подтверждённая оператором.</param>
/// <param name="RecordedAt">Время записи из метаданных файла.</param>
/// <param name="FrameRate">Частота кадров видео.</param>
/// <param name="FrameWidth">Ширина кадра.</param>
/// <param name="FrameHeight">Высота кадра.</param>
/// <param name="UploadedByUserId">Кто загрузил (номер учётной записи источника).</param>
/// <param name="SourceAssetId">Носитель, из которого получен этот (снимок кадра, правленая копия).</param>
/// <param name="SourceTimestampMs">Таймкод кадра в исходном носителе (снимок кадра).</param>
/// <param name="EditOperations">Операции правки (правленая копия фото).</param>
/// <param name="Classification">Гриф.</param>
/// <param name="DivisionId">Подразделение (номер источника).</param>
/// <param name="CreatedAt">Когда загружен.</param>
public sealed record MediaExchangeAsset(
    int AssetId,
    MediaKind Kind,
    string OriginalFileName,
    string ContentType,
    string ContentHash,
    long ByteSize,
    long? DurationMs,
    string? Source,
    DateTimeOffset? CapturedAt,
    DateTimeOffset? RecordedAt,
    double? FrameRate,
    int? FrameWidth,
    int? FrameHeight,
    int? UploadedByUserId,
    int? SourceAssetId,
    long? SourceTimestampMs,
    string? EditOperations,
    short Classification,
    int DivisionId,
    DateTime CreatedAt);

/// <summary>Лицо для пакета обмена: где оно на носителе. Опорных точек, качества и шаблона нет (ТБ-076).</summary>
/// <param name="FaceId">Номер лица в источнике.</param>
/// <param name="AssetId">Носитель.</param>
/// <param name="FrameIndex">Номер кадра (фото — 0).</param>
/// <param name="TimestampMs">Таймкод кадра (фото — 0).</param>
/// <param name="BoxX">Рамка: левый край, пиксели исходного кадра.</param>
/// <param name="BoxY">Рамка: верхний край.</param>
/// <param name="BoxWidth">Рамка: ширина.</param>
/// <param name="BoxHeight">Рамка: высота.</param>
public sealed record MediaExchangeFace(
    int FaceId, int AssetId, int FrameIndex, long TimestampMs, float BoxX, float BoxY, float BoxWidth, float BoxHeight);

/// <summary>Решение верификации для пакета обмена (провенанс появления, ТФ-ОБМ-01).</summary>
/// <param name="CandidateId">Кандидат.</param>
/// <param name="Stage">Этап: эксперт, верификатор, руководитель.</param>
/// <param name="Verdict">Вердикт.</param>
/// <param name="Rationale">Обоснование.</param>
/// <param name="DecidedAtUtc">Когда.</param>
/// <param name="UserId">Кто (номер учётной записи источника).</param>
/// <param name="ConfirmationMode">Режим подтверждения на момент решения.</param>
public sealed record MediaExchangeDecision(
    int CandidateId,
    VerificationStage Stage,
    VerificationVerdict Verdict,
    string Rationale,
    DateTime DecidedAtUtc,
    int UserId,
    ConfirmationMode ConfirmationMode);
