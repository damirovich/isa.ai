using System;
using System.Collections.Generic;
using System.Linq;
using ISC.AI.Modules.Media.Domain.Model;

namespace ISC.AI.Evals.Speech;

/// <summary>Откуда взята длительность звука записи (знаменатель RTF).</summary>
public enum DurationSource
{
    /// <summary>Длительность неизвестна: RTF не считается.</summary>
    Unknown,

    /// <summary>Измерена по файлу (<see cref="IAudioDurationProbe"/>): RTF точный.</summary>
    Measured,

    /// <summary>
    /// Сообщена распознавателем (<c>AudioTranscription.DurationMs</c> — длительность подготовленного звука всей
    /// записи, а не конец последней речи): RTF точный. Берётся, когда измерить файл не удалось (нет ffprobe).
    /// </summary>
    Transcriber,

    /// <summary>
    /// Оценка снизу — конец последнего фрагмента речи (тишина в конце записи не учтена): RTF завышен и
    /// помечается в отчёте как приблизительный.
    /// </summary>
    LastSegmentEnd,
}

/// <summary>Итог оценки одной записи.</summary>
/// <param name="Recording">Запись набора.</param>
/// <param name="Segments">
/// Фрагменты в порядке номеров: выданные распознавателем или — для готового выхода сторонней модели — по одному
/// на непустую строку файла, без таймкодов (начало и конец 0).
/// </param>
/// <param name="Hypothesis">Склеенный через пробел дословный текст фрагментов (первичный слой, как есть).</param>
/// <param name="NormalizedReference">
/// Эталон после <see cref="SpeechTextNormalizer.NormalizeReference"/> (пометки <c>[неразборчиво]</c> сохранены).
/// </param>
/// <param name="NormalizedHypothesis">Гипотеза после <see cref="SpeechTextNormalizer"/>.</param>
/// <param name="Words">Ошибки по словам (WER).</param>
/// <param name="Characters">Ошибки по символам (CER).</param>
/// <param name="Elapsed">
/// Время распознавания: от вызова распознавателя до последнего фрагмента, включая запуск процесса,
/// загрузку модели и ffmpeg — так, как это ощутит пользователь. У готового выхода (<paramref name="Precomputed"/>)
/// не измеряется и равно нулю.
/// </param>
/// <param name="AudioDuration">Длительность звука, если известна.</param>
/// <param name="DurationSource">Откуда взята длительность.</param>
/// <param name="Error">Причина сбоя распознавания; <see langword="null"/> — запись оценена.</param>
/// <param name="Precomputed">
/// Гипотеза взята готовой из <c>hyp/&lt;модель&gt;/</c> (<see cref="SpeechEvalExternalHypotheses"/>), а не получена
/// вызовом распознавателя: время и RTF не измерялись.
/// </param>
public sealed record SpeechEvalRecordResult(
    SpeechEvalRecording Recording,
    IReadOnlyList<TranscriptSegmentDraft> Segments,
    string Hypothesis,
    string NormalizedReference,
    string NormalizedHypothesis,
    EditCounts Words,
    EditCounts Characters,
    TimeSpan Elapsed,
    TimeSpan? AudioDuration,
    DurationSource DurationSource,
    string? Error,
    bool Precomputed = false)
{
    /// <summary>Запись распознана и оценена (сбойные записи в метрики не входят).</summary>
    public bool Succeeded => Error is null;

    /// <summary>
    /// WER записи; <see langword="null"/> — сбой, контрольный файл без речи или эталон целиком из пометок
    /// <c>[неразборчиво]</c> (оценивать нечего).
    /// </summary>
    public double? WordErrorRate => Succeeded ? Words.Rate : null;

    /// <summary>CER записи; <see langword="null"/> — в тех же случаях, что <see cref="WordErrorRate"/>.</summary>
    public double? CharacterErrorRate => Succeeded ? Characters.Rate : null;

    /// <summary>
    /// Фрагменты, в которых после нормализации есть хотя бы одно слово. На контрольном файле без речи это
    /// «ложные фрагменты» (методика пилота, 6.6): модель «услышала» текст там, где его нет.
    /// </summary>
    public int FragmentsWithText => Segments.Count(segment => SpeechTextNormalizer.Words(segment.Text).Count > 0);

    /// <summary>
    /// Коэффициент реального времени RTF = время распознавания / длительность звука: 0,1 — час записи за
    /// 6 минут; больше 1 — медленнее реального времени. <see langword="null"/> — длительность неизвестна или
    /// гипотеза готовая (время не измерялось).
    /// </summary>
    public double? RealTimeFactor =>
        !Precomputed && AudioDuration is { } duration && duration > TimeSpan.Zero ? Elapsed / duration : null;
}

/// <summary>
/// Сводка по группе речевых записей (язык, условия или их сочетание). Метрики — по корпусу группы:
/// WER = Σ ошибок по словам / Σ слов эталона — это и есть среднее WER записей, взвешенное по числу слов
/// эталона; CER — так же по символам эталона. Короткое голосовое не весит столько же, сколько часовой допрос.
/// Контрольные файлы без речи в эти сводки не входят (<see cref="SpeechEvalNoSpeechSummary"/>), записи «только
/// для скорости» — тоже: у них своя сводка той же формы (<see cref="SpeechEvalAggregator.SpeedOnly"/>), где
/// WER/CER не определены.
/// </summary>
/// <param name="Key">Ключ группы (код языка, условий или «язык/условия»).</param>
/// <param name="DisplayName">Название группы по-русски.</param>
/// <param name="Records">Записей в группе, включая сбойные.</param>
/// <param name="Failed">Записей со сбоем распознавания (в метрики не входят).</param>
/// <param name="Words">Суммарные ошибки по словам оценённых записей.</param>
/// <param name="Characters">Суммарные ошибки по символам оценённых записей.</param>
/// <param name="Elapsed">
/// Суммарное время распознавания оценённых записей группы (время до сбоя о скорости не говорит);
/// <see langword="null"/> — время не измерялось (готовый выход сторонней модели).
/// </param>
/// <param name="Audio">Суммарная длительность звука оценённых записей, у которых она известна (и у готового выхода).</param>
/// <param name="TimedAudio">
/// Длительность звука тех из них, у которых измерялось и время распознавания, — знаменатель RTF.
/// </param>
/// <param name="TimedElapsed">Суммарное время распознавания тех же записей — числитель RTF.</param>
/// <param name="DurationApproximate">
/// Хотя бы у одной записи длительность оценена по концу последнего фрагмента (<see cref="DurationSource.LastSegmentEnd"/>).
/// </param>
/// <param name="UnintelligibleRegions">
/// Пометок <c>[неразборчиво]</c> в эталонах оценённых записей группы (протокол пилота 10.1, раздел 2).
/// </param>
public sealed record SpeechEvalGroupSummary(
    string Key,
    string DisplayName,
    int Records,
    int Failed,
    EditCounts Words,
    EditCounts Characters,
    TimeSpan? Elapsed,
    TimeSpan Audio,
    TimeSpan TimedAudio,
    TimeSpan TimedElapsed,
    bool DurationApproximate,
    int UnintelligibleRegions = 0)
{
    /// <summary>Оценённых записей.</summary>
    public int Evaluated => Records - Failed;

    /// <summary>WER группы (взвешенный по словам эталона); <see langword="null"/> — оценённых записей или слов нет.</summary>
    public double? WordErrorRate => Evaluated == 0 ? null : Words.Rate;

    /// <summary>CER группы (взвешенный по символам эталона); <see langword="null"/> — оценённых записей или символов нет.</summary>
    public double? CharacterErrorRate => Evaluated == 0 ? null : Characters.Rate;

    /// <summary>RTF группы: Σ времени / Σ длительности по записям с известной длительностью.</summary>
    public double? RealTimeFactor => TimedAudio > TimeSpan.Zero ? TimedElapsed / TimedAudio : null;
}

/// <summary>
/// Сводка по контрольным файлам без речи (методика пилота, 3.4 и 6.6): сколько слов и фрагментов модель
/// «услышала» в тишине, музыке и шуме. В WER не входит — делить не на что, а правдоподобный текст из ничего
/// опаснее бессмысленного набора букв: его примут за сказанное.
/// </summary>
/// <param name="Records">Контрольных файлов, включая сбойные.</param>
/// <param name="Failed">Файлов со сбоем распознавания (в счёт не входят).</param>
/// <param name="FalseWords">Ложных слов — всех слов гипотезы по оценённым файлам.</param>
/// <param name="FalseFragments">Ложных фрагментов — фрагментов с текстом по оценённым файлам.</param>
/// <param name="MeasuredAudio">
/// Суммарная длительность оценённых файлов, измеренная по самим файлам или сообщённая распознавателем.
/// </param>
/// <param name="MeasuredFalseWords">Ложных слов в файлах с известной длительностью — числитель «на 10 минут».</param>
/// <param name="MeasuredFalseFragments">Ложных фрагментов в тех же файлах.</param>
/// <param name="UnmeasuredRecords">
/// Оценённых файлов без известной длительности (нет ffprobe, а распознаватель её не сообщил — например, у
/// готового выхода сторонней модели): в показатели «на 10 минут» не входят.
/// </param>
public sealed record SpeechEvalNoSpeechSummary(
    int Records,
    int Failed,
    int FalseWords,
    int FalseFragments,
    TimeSpan MeasuredAudio,
    int MeasuredFalseWords,
    int MeasuredFalseFragments,
    int UnmeasuredRecords)
{
    /// <summary>Оценённых файлов.</summary>
    public int Evaluated => Records - Failed;

    /// <summary>Ложных фрагментов на 10 минут неречевого звука (порог методики, 10.2); <see langword="null"/> — длительность неизвестна.</summary>
    public double? FalseFragmentsPer10Minutes => Per10Minutes(MeasuredFalseFragments);

    /// <summary>Ложных слов на 10 минут неречевого звука; <see langword="null"/> — длительность неизвестна.</summary>
    public double? FalseWordsPer10Minutes => Per10Minutes(MeasuredFalseWords);

    private double? Per10Minutes(int count) =>
        MeasuredAudio > TimeSpan.Zero ? count / MeasuredAudio.TotalMinutes * 10 : null;
}

/// <summary>Ход прогона — для вывода в консоль или журнал теста.</summary>
/// <param name="Completed">Записей обработано.</param>
/// <param name="Total">Всего записей.</param>
/// <param name="Last">Итог только что обработанной записи.</param>
public sealed record SpeechEvalProgress(int Completed, int Total, SpeechEvalRecordResult Last);

/// <summary>Итог прогона эталонного набора через распознаватель (КИ-10).</summary>
/// <param name="ModelVersion">
/// Версия модели распознавателя (<c>gigaam-multilingual-ctc@&lt;12 символов SHA-256&gt;</c>) или имя папки
/// готового выхода сторонней модели — результаты разных моделей не сравнимы.
/// </param>
/// <param name="DatasetFolder">Папка набора.</param>
/// <param name="StartedAt">Начало прогона (UTC).</param>
/// <param name="TotalElapsed">Длительность всего прогона.</param>
/// <param name="Machine">Описание машины: RTF зависит от процессора.</param>
/// <param name="Records">Итоги записей в порядке манифеста.</param>
/// <param name="DatasetWarnings">Замечания к набору (эталоны с цифрами, латиница вместо киргизских букв и т. п.).</param>
/// <param name="HypothesisFolder">
/// Папка готового выхода сторонней модели (<c>&lt;набор&gt;/hyp/&lt;модель&gt;</c>), если гипотезы взяты из неё;
/// <see langword="null"/> — гипотезы получены вызовом распознавателя.
/// </param>
public sealed record SpeechEvalReport(
    string ModelVersion,
    string DatasetFolder,
    DateTimeOffset StartedAt,
    TimeSpan TotalElapsed,
    string Machine,
    IReadOnlyList<SpeechEvalRecordResult> Records,
    IReadOnlyList<string> DatasetWarnings,
    string? HypothesisFolder = null);
