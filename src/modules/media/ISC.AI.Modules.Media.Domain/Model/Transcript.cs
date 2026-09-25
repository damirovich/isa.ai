using System.Collections.Generic;

namespace ISC.AI.Modules.Media.Domain.Model;

/// <summary>
/// Фрагмент расшифровки, как его выдаёт модель распознавания речи (ADR-0026): участок речи между паузами
/// и его текст. Таймкоды — от начала записи, по границам детектора речи (VAD).
/// </summary>
/// <param name="Index">Порядковый номер фрагмента в записи, с нуля.</param>
/// <param name="StartMs">Начало фрагмента, мс от начала записи.</param>
/// <param name="EndMs">Конец фрагмента, мс от начала записи.</param>
/// <param name="Text">
/// Текст ДОСЛОВНО, как распознала модель: язык речи (русский, киргизский или вперемешку), строчными
/// буквами, без знаков препинания. Это первичный слой — никакая последующая обработка его не заменяет.
/// </param>
public sealed record TranscriptSegmentDraft(int Index, long StartMs, long EndMs, string Text);

/// <summary>
/// Итог прогона распознавателя (<see cref="Services.IAudioTranscriber"/>): фрагменты по порядку и
/// длительность записи.
/// </summary>
/// <param name="Segments">Фрагменты речи в порядке записи (могут быть пустыми по тексту — фильтрует конвейер).</param>
/// <param name="DurationMs">
/// Длительность ЗАПИСИ (подготовленного звука), мс, как её измерил распознаватель, — не конец последней
/// речи. <see langword="null"/> — длительность неизвестна (например, у видео нет звуковой дорожки).
/// </param>
public sealed record AudioTranscription(IReadOnlyList<TranscriptSegmentDraft> Segments, long? DurationMs)
{
    /// <summary>Пустой итог: звука нет — говорить в записи некому.</summary>
    public static AudioTranscription NoAudio { get; } = new([], null);
}

/// <summary>Метаданные носителя для фоновой расшифровки — без контекста доступа (конвейер от имени системы).</summary>
/// <param name="AssetId">Носитель.</param>
/// <param name="Kind">Вид: расшифровываются аудио и видео.</param>
/// <param name="StoredFileName">Имя исходника в хранилище (категория <c>media-originals</c>).</param>
/// <param name="Classification">Гриф носителя — наследуется фрагментами (ТБ-020).</param>
/// <param name="DivisionId">Подразделение носителя.</param>
public sealed record MediaAssetTranscriptionInfo(
    int AssetId, MediaKind Kind, string StoredFileName, short Classification, int DivisionId);

/// <summary>Итог расшифровки для фоновой задачи: что показать в карточке и записать в журнал.</summary>
/// <param name="Success">Расшифровка записана.</param>
/// <param name="Segments">Сколько фрагментов речи найдено.</param>
/// <param name="Error">Причина неудачи.</param>
public sealed record MediaTranscriptionResult(bool Success, int Segments, string? Error = null);

/// <summary>Фрагмент расшифровки для показа (под решёткой носителя).</summary>
/// <param name="Index">Номер фрагмента.</param>
/// <param name="StartMs">Начало, мс.</param>
/// <param name="EndMs">Конец, мс.</param>
/// <param name="Text">Дословный текст модели.</param>
public sealed record TranscriptSegmentRow(int Index, long StartMs, long EndMs, string Text);

/// <summary>Расшифровка носителя для карточки (ADR-0026).</summary>
/// <param name="AssetId">Носитель.</param>
/// <param name="Status">Состояние расшифровки.</param>
/// <param name="Error">Причина неудачи, если была.</param>
/// <param name="ModelVersion">Модель, которой сделана расшифровка (для акта и воспроизводимости).</param>
/// <param name="TranscribedAt">Когда сделана (UTC).</param>
/// <param name="Segments">Фрагменты по порядку.</param>
public sealed record MediaTranscript(
    int AssetId,
    TranscriptStatus Status,
    string? Error,
    string? ModelVersion,
    DateTime? TranscribedAt,
    IReadOnlyList<TranscriptSegmentRow> Segments);

/// <summary>Найденный по словам фрагмент расшифровки (поиск по материалам дела).</summary>
/// <param name="AssetId">Носитель.</param>
/// <param name="AssetFileName">Исходное имя файла — для подписи в выдаче.</param>
/// <param name="Kind">Вид носителя (аудио/видео).</param>
/// <param name="SegmentIndex">Номер фрагмента.</param>
/// <param name="StartMs">Начало фрагмента, мс — для перехода к месту записи.</param>
/// <param name="EndMs">Конец фрагмента, мс.</param>
/// <param name="Text">Текст фрагмента.</param>
public sealed record TranscriptHit(
    int AssetId, string AssetFileName, MediaKind Kind, int SegmentIndex, long StartMs, long EndMs, string Text);
