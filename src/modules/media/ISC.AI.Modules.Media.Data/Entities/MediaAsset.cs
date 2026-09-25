using ISC.AI.Abstractions.Entities;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Domain.Model;

namespace ISC.AI.Modules.Media.Data.Entities;

/// <summary>
/// Носитель (<c>media.asset</c>, ТС-010): фото, видео или аудио (ADR-0026), принятое в хранилище. Байты лежат в
/// <c>IFileStorage</c> ядра (категория <c>media-originals</c>, подкаталог = идентификатор), здесь —
/// метаданные, хеш и режимные поля. Физически удаляемый носитель биометрии (ТБ-064/075):
/// намеренно НЕ <c>ISoftDeletable</c>.
/// </summary>
public class MediaAsset : AuditableEntity, IClassified
{
    /// <summary>Вид носителя.</summary>
    public MediaKind Kind { get; set; }

    /// <summary>Исходное имя файла (только для отображения).</summary>
    public required string OriginalFileName { get; set; }

    /// <summary>Имя файла в хранилище (GUID + расширение; исходное имя на диск не попадает).</summary>
    public required string StoredFileName { get; set; }

    /// <summary>MIME-тип, определённый по содержимому.</summary>
    public required string ContentType { get; set; }

    /// <summary>SHA-256 содержимого (hex, верхний регистр) — дедупликация и целостность (ТБ-074).</summary>
    public required string ContentHash { get; set; }

    /// <summary>Размер, байт.</summary>
    public long ByteSize { get; set; }

    /// <summary>Длительность видео или аудио, мс; <see langword="null"/> — изображение либо ещё не известна.</summary>
    public long? DurationMs { get; set; }

    /// <summary>Источник/происхождение записи (свободный текст, ТФ-МЕД).</summary>
    public string? Source { get; set; }

    /// <summary>Дата/время съёмки, если известны.</summary>
    public DateTimeOffset? CapturedAt { get; set; }

    /// <summary>Гриф носителя (ТБ-020). NOT NULL.</summary>
    public short Classification { get; set; }

    /// <summary>Подразделение-владелец (ТБ-020). NOT NULL.</summary>
    public int DivisionId { get; set; }

    /// <summary>Актуален ли носитель для поиска по умолчанию (ADR-0013).</summary>
    public bool IsCurrent { get; set; } = true;

    /// <summary>Кто загрузил — слабая ссылка на <c>core.app_user</c> (ТО-инф-06), без FK через схемы.</summary>
    public int? UploadedByUserId { get; set; }

    /// <summary>Состояние индексации (ТП-004).</summary>
    public MediaIndexStatus IndexStatus { get; set; } = MediaIndexStatus.Uploaded;

    /// <summary>Причина неудачи индексации (при <see cref="MediaIndexStatus.Failed"/>).</summary>
    public string? IndexError { get; set; }

    /// <summary>Версия детектора, которой построены лица (ТО-прог-11).</summary>
    public string? DetectorVersion { get; set; }

    /// <summary>Версия векторизатора, которой построены шаблоны (ТО-прог-11).</summary>
    public string? EmbedderVersion { get; set; }

    /// <summary>Когда завершена индексация (UTC).</summary>
    public DateTime? IndexedAt { get; set; }

    /// <summary>
    /// Состояние расшифровки речи (ADR-0026). Отдельно от <see cref="IndexStatus"/>: другой конвейер, другая
    /// модель, другой сбой. По умолчанию — неприменима (изображение; носители до ADR-0026).
    /// </summary>
    public TranscriptStatus TranscriptStatus { get; set; } = TranscriptStatus.NotApplicable;

    /// <summary>Причина неудачи расшифровки (при <see cref="TranscriptStatus.Failed"/>).</summary>
    public string? TranscriptError { get; set; }

    /// <summary>Модель, которой сделана текущая расшифровка (с пином файлов).</summary>
    public string? TranscriberVersion { get; set; }

    /// <summary>Когда записана текущая расшифровка (UTC).</summary>
    public DateTime? TranscribedAt { get; set; }

    /// <summary>
    /// НАТИВНАЯ частота кадров видео, к/с (проба ffprobe при индексации, ADR-0028) — по ней считается номер кадра
    /// и шаг покадрового просмотра. <see langword="null"/> — не видео либо проба ещё не выполнялась (носители до
    /// ADR-0028: заполняется при «Переиндексировать»). Не путать с частотой ВЫБОРКИ раскадровки.
    /// </summary>
    public double? FrameRate { get; set; }

    /// <summary>Ширина кадра видео после автоповорота по метке контейнера, пиксели (ADR-0028).</summary>
    public int? FrameWidth { get; set; }

    /// <summary>Высота кадра видео после автоповорота, пиксели (ADR-0028).</summary>
    public int? FrameHeight { get; set; }

    /// <summary>
    /// Носитель-источник для ПРОИЗВОДНОГО носителя — снимка кадра видео (ADR-0028). Слабая ссылка без FK, как
    /// <see cref="UploadedByUserId"/>: уничтожение видео снимок не трогает и наоборот — у каждого своя запись
    /// журнала и свой акт (ТБ-064/075); FK с каскадом сделал бы удаление снимка «безмолвным».
    /// </summary>
    public int? SourceAssetId { get; set; }

    /// <summary>Момент записи источника, мс, из которого снят кадр (для снимка кадра).</summary>
    public long? SourceTimestampMs { get; set; }
}
