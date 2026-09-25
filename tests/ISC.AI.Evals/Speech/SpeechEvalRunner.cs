using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;

namespace ISC.AI.Evals.Speech;

/// <summary>
/// Прогон эталонного набора (КИ-10, ADR-0026): для каждой записи — гипотеза, склейка фрагментов, WER/CER против
/// эталона, время и RTF. Гипотеза берётся либо у распознавателя через порт <see cref="IAudioTranscriber"/>,
/// либо готовой из <c>hyp/&lt;модель&gt;/</c> (<see cref="SpeechEvalExternalHypotheses"/>) — для сторонних моделей.
/// </summary>
/// <remarks>
/// <para>Распознаватель передаётся извне: в пилоте — рабочая реализация <c>ISC.AI.Speech</c> с нужной моделью
/// (GigaAM 220M/600M), в тестах — подделка. Vosk и Whisper системой не запускаются: их выход, полученный
/// на стенде их же утилитами, оценивается вторым конструктором. Сравнение моделей — это несколько прогонов
/// одного набора.</para>
/// <para>Записи обрабатываются ПОСЛЕДОВАТЕЛЬНО: рабочая реализация всё равно пропускает один процесс за раз,
/// а параллельный прогон исказил бы время каждой записи. Сбой одной записи не останавливает прогон — он
/// попадает в отчёт; отмена останавливает прогон целиком.</para>
/// </remarks>
public sealed class SpeechEvalRunner
{
    private readonly IAudioTranscriber? _transcriber;
    private readonly SpeechEvalExternalHypotheses? _external;
    private readonly IAudioDurationProbe? _durationProbe;
    private readonly TimeProvider _time;

    /// <summary>Прогон через распознаватель: гипотеза — его выход, время и RTF измеряются.</summary>
    /// <param name="transcriber">Распознаватель речи.</param>
    /// <param name="durationProbe">
    /// Измеритель длительности звука; <see langword="null"/> или неудача — длительность, которую сообщил
    /// распознаватель, а если нет и её — конец последнего фрагмента (оценка снизу).
    /// </param>
    /// <param name="timeProvider">Часы (подменяются в тестах); по умолчанию — системные.</param>
    public SpeechEvalRunner(
        IAudioTranscriber transcriber,
        IAudioDurationProbe? durationProbe = null,
        TimeProvider? timeProvider = null)
    {
        _transcriber = transcriber ?? throw new ArgumentNullException(nameof(transcriber));
        _durationProbe = durationProbe;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Оценка готового выхода сторонней модели: распознаватель не вызывается, время и RTF не измеряются;
    /// длительность звука (для ложных фрагментов на 10 минут) измеряется как обычно.
    /// </summary>
    /// <param name="hypotheses">Готовый выход модели, прочитанный для этого набора.</param>
    /// <param name="durationProbe">Измеритель длительности звука; <see langword="null"/> — длительность неизвестна.</param>
    /// <param name="timeProvider">Часы (подменяются в тестах); по умолчанию — системные.</param>
    public SpeechEvalRunner(
        SpeechEvalExternalHypotheses hypotheses,
        IAudioDurationProbe? durationProbe = null,
        TimeProvider? timeProvider = null)
    {
        _external = hypotheses ?? throw new ArgumentNullException(nameof(hypotheses));
        _durationProbe = durationProbe;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Прогоняет записи набора и возвращает отчёт. При готовом выходе — только записи, для которых он есть
    /// (остальные перечислены в замечаниях отчёта).
    /// </summary>
    /// <param name="dataset">Эталонный набор.</param>
    /// <param name="progress">Ход прогона (необязателен).</param>
    /// <param name="cancellationToken">Отмена прогона.</param>
    public async Task<SpeechEvalReport> RunAsync(
        SpeechEvalDataset dataset,
        IProgress<SpeechEvalProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataset);

        var recordings = _external is null
            ? dataset.Recordings
            : dataset.Recordings.Where(recording => _external.Texts.ContainsKey(recording.File)).ToList();
        var startedAt = _time.GetUtcNow();
        var started = _time.GetTimestamp();
        var results = new List<SpeechEvalRecordResult>(recordings.Count);
        foreach (var recording in recordings)
        {
            var result = await EvaluateAsync(recording, cancellationToken).ConfigureAwait(false);
            results.Add(result);
            progress?.Report(new SpeechEvalProgress(results.Count, recordings.Count, result));
        }

        return new SpeechEvalReport(
            _external?.Model ?? _transcriber!.ModelVersion, // один из двух задан конструктором
            dataset.RootFolder,
            startedAt,
            _time.GetElapsedTime(started),
            DescribeMachine(),
            results,
            _external is null ? dataset.Warnings : [.. dataset.Warnings, .. _external.Warnings],
            _external?.Folder);
    }

    /// <summary>Получает гипотезу одной записи и сравнивает с эталоном.</summary>
    /// <param name="recording">Запись набора.</param>
    /// <param name="cancellationToken">Отмена.</param>
    /// <exception cref="OperationCanceledException">Прогон отменён.</exception>
    /// <exception cref="KeyNotFoundException">Готовый выход модели не содержит этой записи.</exception>
    public async Task<SpeechEvalRecordResult> EvaluateAsync(SpeechEvalRecording recording, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recording);

        var measured = await MeasureDurationAsync(recording.AudioPath, cancellationToken).ConfigureAwait(false);

        var segments = new List<TranscriptSegmentDraft>();
        long? reportedDurationMs = null;
        string? error = null;
        var elapsed = TimeSpan.Zero;
        if (_external is not null)
        {
            segments.AddRange(SpeechEvalExternalHypotheses.ToSegments(_external.Texts[recording.File]));
        }
        else
        {
            var started = _time.GetTimestamp();
            try
            {
                var transcription = await _transcriber!.TranscribeAsync(recording.AudioPath, cancellationToken) // задан конструктором
                    .ConfigureAwait(false);
                segments.AddRange(transcription.Segments);
                reportedDurationMs = transcription.DurationMs;
            }
            catch (Exception exception) when (!(exception is OperationCanceledException && cancellationToken.IsCancellationRequested))
            {
                // Сбой записи (битый файл, ошибка процесса-распознавателя) — в отчёт, прогон продолжается.
                error = $"{exception.GetType().Name}: {exception.Message}";
            }

            elapsed = _time.GetElapsedTime(started);
        }

        var ordered = segments.OrderBy(segment => segment.Index).ToList();
        TimeSpan? duration = null;
        var source = DurationSource.Unknown;
        if (measured is not null)
        {
            duration = measured;
            source = DurationSource.Measured;
        }
        else if (reportedDurationMs is > 0)
        {
            // Длительность всей записи, которую распознаватель измерил сам (строка done) — точная, не по речи.
            duration = TimeSpan.FromMilliseconds(reportedDurationMs.Value);
            source = DurationSource.Transcriber;
        }
        else if (_external is null && ordered.Count > 0)
        {
            // Последний довод — оценка снизу: тишина после последнего фрагмента не учтена, RTF будет помечен как
            // приблизительный. У готового выхода таймкодов нет — там оценки нет.
            duration = TimeSpan.FromMilliseconds(ordered.Max(segment => segment.EndMs));
            source = DurationSource.LastSegmentEnd;
        }

        var normalizedReference = SpeechTextNormalizer.NormalizeReference(recording.ReferenceText);
        var precomputed = _external is not null;
        if (error is not null)
        {
            return new SpeechEvalRecordResult(
                recording, ordered, string.Empty, normalizedReference, string.Empty,
                default, default, elapsed, duration, source, error, precomputed);
        }

        var hypothesis = string.Join(' ', ordered.Select(segment => segment.Text?.Trim()).Where(text => !string.IsNullOrEmpty(text)));
        var words = ErrorRateCalculator.WordErrors(recording.ReferenceText, hypothesis, cancellationToken);
        var characters = ErrorRateCalculator.CharacterErrors(recording.ReferenceText, hypothesis, cancellationToken);

        return new SpeechEvalRecordResult(
            recording,
            ordered,
            hypothesis,
            normalizedReference,
            SpeechTextNormalizer.Normalize(hypothesis),
            words,
            characters,
            elapsed,
            duration,
            source,
            Error: null,
            precomputed);
    }

    private async Task<TimeSpan?> MeasureDurationAsync(string path, CancellationToken cancellationToken)
    {
        if (_durationProbe is null)
        {
            return null;
        }

        try
        {
            var duration = await _durationProbe.GetDurationAsync(path, cancellationToken).ConfigureAwait(false);
            return duration > TimeSpan.Zero ? duration : null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Длительность нужна только для RTF и счёта «на 10 минут»: её сбой не должен срывать оценку качества.
            return null;
        }
    }

    private static string DescribeMachine() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{Environment.ProcessorCount} логических процессоров; {RuntimeInformation.OSDescription}; {RuntimeInformation.ProcessArchitecture}; {RuntimeInformation.FrameworkDescription}");
}
