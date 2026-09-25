using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Evals.Speech;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using Shouldly;
using Xunit;

namespace ISC.AI.Evals.Speech.Tests;

/// <summary>
/// Прогон набора через порт <see cref="IAudioTranscriber"/> (КИ-10): склейка фрагментов, WER/CER, время и RTF,
/// сбой записи не останавливает прогон, сводки взвешены по словам и раздельны по языку.
/// </summary>
public sealed class SpeechEvalRunnerTests
{
    private readonly ManualClock _clock = new();

    [Fact(DisplayName = "Прогон: фрагменты склеиваются по номеру, WER/CER против эталона, RTF = время / длительность")]
    public async Task Evaluates_recording()
    {
        var transcriber = new ScriptedTranscriber(_clock)
        {
            ["a.wav"] = new(TimeSpan.FromSeconds(3),
            [
                new TranscriptSegmentDraft(1, 4_000, 9_000, "бала жок"),
                new TranscriptSegmentDraft(0, 500, 3_500, "уйдо"),
            ]),
        };
        var probe = new FixedDurations { ["a.wav"] = TimeSpan.FromSeconds(30) };
        var dataset = Dataset(Recording("a.wav", SpeechLanguage.Kyrgyz, RecordingCondition.Phone, "Үйдө бала жок."));

        var report = await new SpeechEvalRunner(transcriber, probe, _clock).RunAsync(dataset);

        report.ModelVersion.ShouldBe(ScriptedTranscriber.Version);
        var result = report.Records.ShouldHaveSingleItem();
        result.Succeeded.ShouldBeTrue();
        result.Hypothesis.ShouldBe("уйдо бала жок");
        result.NormalizedReference.ShouldBe("үйдө бала жок");
        result.Words.Substitutions.ShouldBe(1);
        result.WordErrorRate!.Value.ShouldBe(1.0 / 3, 1e-12);
        result.CharacterErrorRate!.Value.ShouldBe(2.0 / 13, 1e-12);
        result.Elapsed.ShouldBe(TimeSpan.FromSeconds(3));
        result.DurationSource.ShouldBe(DurationSource.Measured);
        result.RealTimeFactor!.Value.ShouldBe(0.1, 1e-12);
    }

    [Fact(DisplayName = "Прогон: без длительности файла RTF считается по концу последнего фрагмента и помечается приблизительным")]
    public async Task Falls_back_to_last_segment_end()
    {
        var transcriber = new ScriptedTranscriber(_clock)
        {
            ["a.wav"] = new(TimeSpan.FromSeconds(2), [new TranscriptSegmentDraft(0, 0, 20_000, "текст")]),
        };
        var dataset = Dataset(Recording("a.wav", SpeechLanguage.Russian, RecordingCondition.Dictaphone, "текст"));

        var report = await new SpeechEvalRunner(transcriber, durationProbe: null, _clock).RunAsync(dataset);

        var result = report.Records.ShouldHaveSingleItem();
        result.DurationSource.ShouldBe(DurationSource.LastSegmentEnd);
        result.RealTimeFactor!.Value.ShouldBe(0.1, 1e-12);
        SpeechEvalAggregator.ByLanguage(report.Records)[0].DurationApproximate.ShouldBeTrue();
    }

    [Fact(DisplayName = "Прогон: сбой распознавания записи попадает в отчёт, остальные записи оцениваются")]
    public async Task Failure_of_one_recording_does_not_stop_run()
    {
        var transcriber = new ScriptedTranscriber(_clock)
        {
            ["ok.wav"] = new(TimeSpan.FromSeconds(1), [new TranscriptSegmentDraft(0, 0, 1_000, "да")]),
        };
        var dataset = Dataset(
            Recording("broken.wav", SpeechLanguage.Russian, RecordingCondition.Phone, "нет"),
            Recording("ok.wav", SpeechLanguage.Russian, RecordingCondition.Phone, "да"));
        var progress = new List<SpeechEvalProgress>();

        var report = await new SpeechEvalRunner(transcriber, timeProvider: _clock)
            .RunAsync(dataset, new SynchronousProgress<SpeechEvalProgress>(progress.Add));

        report.Records.Count.ShouldBe(2);
        report.Records[0].Succeeded.ShouldBeFalse();
        report.Records[0].Error!.ShouldContain("сбой модели");
        report.Records[0].WordErrorRate.ShouldBeNull();
        report.Records[1].WordErrorRate.ShouldBe(0.0);
        progress.Select(item => item.Completed).ShouldBe([1, 2]);

        var russian = SpeechEvalAggregator.ByLanguage(report.Records).Single(group => group.Key == "ru");
        russian.Records.ShouldBe(2);
        russian.Failed.ShouldBe(1);
        russian.WordErrorRate.ShouldBe(0.0); // сбойная запись в метрики не входит
    }

    [Fact(DisplayName = "Прогон: отмена останавливает прогон целиком, а не записывается как сбой записи")]
    public async Task Cancellation_propagates()
    {
        using var cancellation = new CancellationTokenSource();
        var transcriber = new ScriptedTranscriber(_clock, onStart: cancellation.Cancel)
        {
            ["a.wav"] = new(TimeSpan.FromSeconds(1), [new TranscriptSegmentDraft(0, 0, 1_000, "да")]),
        };
        var dataset = Dataset(Recording("a.wav", SpeechLanguage.Russian, RecordingCondition.Phone, "да"));

        await Should.ThrowAsync<OperationCanceledException>(
            () => new SpeechEvalRunner(transcriber, timeProvider: _clock).RunAsync(dataset, cancellationToken: cancellation.Token));
    }

    [Fact(DisplayName = "Сводка: WER группы взвешен по словам эталона, языки раздельно, все три языка видны")]
    public async Task Summaries_are_weighted_by_reference_words_and_split_by_language()
    {
        var transcriber = new ScriptedTranscriber(_clock)
        {
            // 10 слов, 1 ошибка («үч» → «уч») — 10 %.
            ["long.wav"] = new(TimeSpan.FromSeconds(1), [new TranscriptSegmentDraft(0, 0, 1_000, "бир эки уч төрт беш алты жети сегиз тогуз он")]),
            // 2 слова, 2 ошибки — 100 %.
            ["short.wav"] = new(TimeSpan.FromSeconds(1), [new TranscriptSegmentDraft(0, 0, 1_000, "икс игрек")]),
            ["ru.wav"] = new(TimeSpan.FromSeconds(1), [new TranscriptSegmentDraft(0, 0, 1_000, "всё верно")]),
        };
        var dataset = Dataset(
            Recording("long.wav", SpeechLanguage.Kyrgyz, RecordingCondition.Interview, "Бир, эки, үч, төрт, беш, алты, жети, сегиз, тогуз, он."),
            Recording("short.wav", SpeechLanguage.Kyrgyz, RecordingCondition.Phone, "бир эки"),
            Recording("ru.wav", SpeechLanguage.Russian, RecordingCondition.Phone, "Всё верно."));

        var report = await new SpeechEvalRunner(transcriber, timeProvider: _clock).RunAsync(dataset);

        report.Records[0].WordErrorRate!.Value.ShouldBe(0.1, 1e-12);
        report.Records[1].WordErrorRate!.Value.ShouldBe(1.0, 1e-12);
        var byLanguage = SpeechEvalAggregator.ByLanguage(report.Records);
        byLanguage.Select(group => group.Key).ShouldBe(["ru", "ky", "mixed"]);
        byLanguage[0].WordErrorRate.ShouldBe(0.0);
        // ky: (1 + 2) / (10 + 2) = 25 %, а не среднее долей (10 % и 100 % → 55 %).
        byLanguage[1].WordErrorRate!.Value.ShouldBe(0.25, 1e-12);
        byLanguage[2].Records.ShouldBe(0);
        byLanguage[2].WordErrorRate.ShouldBeNull();

        var byCondition = SpeechEvalAggregator.ByCondition(report.Records);
        byCondition.Select(group => group.Key).ShouldBe(["phone", "interview"]);
        SpeechEvalAggregator.ByLanguageAndCondition(report.Records).Select(group => group.Key)
            .ShouldBe(["ru/phone", "ky/phone", "ky/interview"]);

        SpeechEvalAggregator.Worst(report.Records, 1).ShouldHaveSingleItem().Recording.File.ShouldBe("short.wav");
    }

    [Fact(DisplayName = "Контрольные файлы без речи: ложные слова и фрагменты отдельно, в WER и сводки по языку и условиям не входят")]
    public async Task No_speech_files_are_counted_as_false_words_not_wer()
    {
        var transcriber = new ScriptedTranscriber(_clock)
        {
            ["ru.wav"] = new(TimeSpan.FromSeconds(1), [new TranscriptSegmentDraft(0, 0, 1_000, "всё верно")]),
            // Музыка: модель «услышала» фразу из двух фрагментов — 3 ложных слова.
            ["music.wav"] = new(TimeSpan.FromSeconds(1),
            [
                new TranscriptSegmentDraft(0, 10_000, 12_000, "спасибо за"),
                new TranscriptSegmentDraft(1, 40_000, 41_000, "просмотр"),
                new TranscriptSegmentDraft(2, 50_000, 51_000, " … "), // без слов — не ложный фрагмент
            ]),
            ["silence.wav"] = new(TimeSpan.FromSeconds(1), []),
        };
        var probe = new FixedDurations
        {
            ["ru.wav"] = TimeSpan.FromSeconds(10),
            ["music.wav"] = TimeSpan.FromMinutes(5),
            ["silence.wav"] = TimeSpan.FromMinutes(5),
        };
        var dataset = Dataset(
            Recording("ru.wav", SpeechLanguage.Russian, RecordingCondition.Phone, "Всё верно."),
            Recording("music.wav", language: null, RecordingCondition.Phone, string.Empty),
            Recording("silence.wav", language: null, RecordingCondition.Dictaphone, string.Empty));

        var report = await new SpeechEvalRunner(transcriber, probe, _clock).RunAsync(dataset);

        var music = report.Records[1];
        music.Succeeded.ShouldBeTrue();
        music.WordErrorRate.ShouldBeNull();
        music.Words.HypothesisLength.ShouldBe(3);
        music.FragmentsWithText.ShouldBe(2);

        var russian = SpeechAggregatorGroup(SpeechEvalAggregator.ByLanguage(report.Records), "ru");
        russian.Records.ShouldBe(1);
        russian.WordErrorRate.ShouldBe(0.0); // галлюцинации на музыке не растворились в WER как вставки
        SpeechEvalAggregator.ByCondition(report.Records).Select(group => group.Key).ShouldBe(["phone"]);
        SpeechAggregatorGroup(SpeechEvalAggregator.ByCondition(report.Records), "phone").Records.ShouldBe(1);
        SpeechEvalAggregator.ByLanguageAndCondition(report.Records).Select(group => group.Key).ShouldBe(["ru/phone"]);
        SpeechEvalAggregator.Worst(report.Records, 5).ShouldBeEmpty();

        var noSpeech = SpeechEvalAggregator.NoSpeech(report.Records).ShouldNotBeNull();
        noSpeech.Records.ShouldBe(2);
        noSpeech.FalseWords.ShouldBe(3);
        noSpeech.FalseFragments.ShouldBe(2);
        noSpeech.MeasuredAudio.ShouldBe(TimeSpan.FromMinutes(10));
        noSpeech.FalseFragmentsPer10Minutes!.Value.ShouldBe(2.0, 1e-12);
        noSpeech.FalseWordsPer10Minutes!.Value.ShouldBe(3.0, 1e-12);

        SpeechEvalAggregator.NoSpeech(report.Records.Take(1)).ShouldBeNull();
    }

    [Fact(DisplayName = "Прогон: [неразборчиво] в эталоне — слова модели на его месте в отчёте записи «не оценены», WER без штрафа")]
    public async Task Unintelligible_regions_are_not_penalized_in_run()
    {
        var transcriber = new ScriptedTranscriber(_clock)
        {
            ["a.wav"] = new(TimeSpan.FromSeconds(1), [new TranscriptSegmentDraft(0, 0, 1_000, "мен кечээ отделениеге бардым")]),
        };
        var dataset = Dataset(Recording("a.wav", SpeechLanguage.Mixed, RecordingCondition.Voice, "Мен [неразборчиво] бардым."));

        var report = await new SpeechEvalRunner(transcriber, timeProvider: _clock).RunAsync(dataset);

        var result = report.Records.ShouldHaveSingleItem();
        result.WordErrorRate.ShouldBe(0.0);
        result.Words.ReferenceLength.ShouldBe(2);
        result.Words.Unscored.ShouldBe(2);
        result.NormalizedReference.ShouldBe("мен [неразборчиво] бардым");
        result.Recording.UnintelligibleRegions.ShouldBe(1);
        var mixed = SpeechAggregatorGroup(SpeechEvalAggregator.ByLanguage(report.Records), "mixed");
        mixed.Words.Unscored.ShouldBe(2);
        mixed.UnintelligibleRegions.ShouldBe(1);
    }

    [Fact(DisplayName = "Длинная запись без эталона (эталон — одна пометка [неразборчиво]): считается отдельно «только скорость», не искажает записи, «Не оцен.» и WER группы")]
    public async Task Long_recording_without_reference_counts_for_speed_only()
    {
        var transcriber = new ScriptedTranscriber(_clock)
        {
            ["short.wav"] = new(TimeSpan.FromSeconds(1), [new TranscriptSegmentDraft(0, 0, 1_000, "бир эки уч")]),
            ["long.wav"] = new(TimeSpan.FromMinutes(20), [new TranscriptSegmentDraft(0, 0, 1_000, "көп сөз бар")]),
            ["far.wav"] = new(TimeSpan.FromMinutes(10), [new TranscriptSegmentDraft(0, 0, 1_000, "дагы сөз")]),
        };
        var probe = new FixedDurations
        {
            ["short.wav"] = TimeSpan.FromSeconds(10),
            ["long.wav"] = TimeSpan.FromHours(1),
            ["far.wav"] = TimeSpan.FromMinutes(30),
        };
        var dataset = Dataset(
            Recording("short.wav", SpeechLanguage.Kyrgyz, RecordingCondition.Interview, "бир эки үч"),
            Recording("long.wav", SpeechLanguage.Kyrgyz, RecordingCondition.Interview, "[неразборчиво]"),
            Recording("far.wav", SpeechLanguage.Kyrgyz, RecordingCondition.Far, "[неразборчиво]"));

        var report = await new SpeechEvalRunner(transcriber, probe, _clock).RunAsync(dataset);

        report.Records[1].Recording.IsSpeedOnly.ShouldBeTrue();
        report.Records[0].Recording.IsSpeedOnly.ShouldBeFalse();
        report.Records[1].WordErrorRate.ShouldBeNull();
        report.Records[1].RealTimeFactor!.Value.ShouldBe(20.0 / 60, 1e-12);

        // Сводка качества — только по короткой записи: ни числа записей, ни «Не оцен.», ни времени длинных.
        var kyrgyz = SpeechAggregatorGroup(SpeechEvalAggregator.ByLanguage(report.Records), "ky");
        kyrgyz.Records.ShouldBe(1);
        kyrgyz.Words.ReferenceLength.ShouldBe(3);
        kyrgyz.Words.Unscored.ShouldBe(0);
        kyrgyz.UnintelligibleRegions.ShouldBe(0);
        kyrgyz.WordErrorRate!.Value.ShouldBe(1.0 / 3, 1e-12); // только «уч» вместо «үч» в короткой записи
        kyrgyz.TimedAudio.ShouldBe(TimeSpan.FromSeconds(10));
        kyrgyz.Elapsed.ShouldBe(TimeSpan.FromSeconds(1));

        // Условие, в котором есть только запись «для скорости», группы качества не образует.
        SpeechEvalAggregator.ByCondition(report.Records).Select(group => group.Key).ShouldBe(["interview"]);
        SpeechEvalAggregator.ByLanguageAndCondition(report.Records).Select(group => group.Key).ShouldBe(["ky/interview"]);
        SpeechEvalAggregator.UnintelligibleHeavy(report.Records).ShouldBeEmpty(); // доля на 0 слов эталона не определена

        // Скорость длинных записей — своей сводкой.
        var speedOnly = SpeechEvalAggregator.SpeedOnly(report.Records).ShouldNotBeNull();
        speedOnly.Records.ShouldBe(2);
        speedOnly.Failed.ShouldBe(0);
        speedOnly.TimedAudio.ShouldBe(TimeSpan.FromMinutes(90));
        speedOnly.Elapsed.ShouldBe(TimeSpan.FromMinutes(30));
        speedOnly.RealTimeFactor!.Value.ShouldBe(1.0 / 3, 1e-12);
        speedOnly.WordErrorRate.ShouldBeNull();
        SpeechEvalAggregator.SpeedOnly(report.Records.Take(1)).ShouldBeNull();
    }

    [Fact(DisplayName = "Записи с большой долей неразборчивого: больше одной пометки на 20 слов эталона, сбойные тоже входят")]
    public async Task Lists_recordings_with_much_unintelligible_speech()
    {
        const string tenWords = "бир эки үч төрт беш алты жети сегиз тогуз он";
        var transcriber = new ScriptedTranscriber(_clock)
        {
            ["heavy.wav"] = new(TimeSpan.FromSeconds(1), [new TranscriptSegmentDraft(0, 0, 1_000, tenWords)]),
            ["light.wav"] = new(TimeSpan.FromSeconds(1), [new TranscriptSegmentDraft(0, 0, 1_000, tenWords + " " + tenWords)]),
            ["clean.wav"] = new(TimeSpan.FromSeconds(1), [new TranscriptSegmentDraft(0, 0, 1_000, tenWords)]),
        };
        var dataset = Dataset(
            Recording("heavy.wav", SpeechLanguage.Kyrgyz, RecordingCondition.Phone, tenWords + " [неразборчиво]"), // 1 на 10 слов
            Recording("light.wav", SpeechLanguage.Kyrgyz, RecordingCondition.Phone, tenWords + " " + tenWords + " [неразборчиво]"), // 1 на 20 — не больше
            Recording("clean.wav", SpeechLanguage.Kyrgyz, RecordingCondition.Phone, tenWords),
            Recording("broken.wav", SpeechLanguage.Russian, RecordingCondition.Phone, "да [неразборчиво] нет")); // сбой — доля по эталону

        var report = await new SpeechEvalRunner(transcriber, timeProvider: _clock).RunAsync(dataset);

        SpeechEvalAggregator.UnintelligibleHeavy(report.Records).Select(record => record.Recording.File)
            .ShouldBe(["heavy.wav", "broken.wav"]);
    }

    [Fact(DisplayName = "Прогон: без измерения файла длительность берётся у распознавателя — RTF точный (без «≈»), годится для «на 10 минут»")]
    public async Task Uses_duration_reported_by_transcriber()
    {
        var transcriber = new ScriptedTranscriber(_clock)
        {
            // Речь кончается на 20-й секунде, запись длится 30 с: оценка по фрагментам занизила бы длительность.
            ["a.wav"] = new(TimeSpan.FromSeconds(3), [new TranscriptSegmentDraft(0, 0, 20_000, "текст")], DurationMs: 30_000),
            ["b.wav"] = new(TimeSpan.FromSeconds(4), [new TranscriptSegmentDraft(0, 0, 1_000, "текст")], DurationMs: 30_000),
            ["hum.wav"] = new(TimeSpan.FromSeconds(1), [new TranscriptSegmentDraft(0, 1_000, 2_000, "ага")], DurationMs: 600_000),
        };
        var probe = new FixedDurations { ["b.wav"] = TimeSpan.FromSeconds(40) }; // измерение файла главнее
        var dataset = Dataset(
            Recording("a.wav", SpeechLanguage.Russian, RecordingCondition.Dictaphone, "текст"),
            Recording("b.wav", SpeechLanguage.Russian, RecordingCondition.Dictaphone, "текст"),
            Recording("hum.wav", language: null, RecordingCondition.Other, string.Empty));

        var report = await new SpeechEvalRunner(transcriber, probe, _clock).RunAsync(dataset);

        var reported = report.Records[0];
        reported.DurationSource.ShouldBe(DurationSource.Transcriber);
        reported.AudioDuration.ShouldBe(TimeSpan.FromSeconds(30));
        reported.RealTimeFactor!.Value.ShouldBe(0.1, 1e-12);
        report.Records[1].DurationSource.ShouldBe(DurationSource.Measured);
        report.Records[1].AudioDuration.ShouldBe(TimeSpan.FromSeconds(40));
        SpeechEvalAggregator.ByLanguage(report.Records)[0].DurationApproximate.ShouldBeFalse();

        var noSpeech = SpeechEvalAggregator.NoSpeech(report.Records).ShouldNotBeNull();
        noSpeech.UnmeasuredRecords.ShouldBe(0);
        noSpeech.MeasuredAudio.ShouldBe(TimeSpan.FromMinutes(10));
        noSpeech.FalseFragmentsPer10Minutes!.Value.ShouldBe(1.0, 1e-12);
    }

    private static SpeechEvalGroupSummary SpeechAggregatorGroup(IEnumerable<SpeechEvalGroupSummary> groups, string key) =>
        groups.Single(group => group.Key == key);

    private static SpeechEvalDataset Dataset(params SpeechEvalRecording[] recordings) =>
        new(Path.Combine(Path.GetTempPath(), "iscai-speech-eval-fake"), recordings, []);

    private static SpeechEvalRecording Recording(string file, SpeechLanguage? language, RecordingCondition condition, string reference) =>
        new(file, Path.Combine(Path.GetTempPath(), "iscai-speech-eval-fake", file), Path.ChangeExtension(file, ".txt"),
            language, condition, Note: string.Empty, reference);

    /// <summary>Часы, которые двигает только тест (время распознавания детерминировано).</summary>
    private sealed class ManualClock : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;

        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 9, 25, 0, 0, 0, TimeSpan.Zero).AddTicks(_ticks);

        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }

    /// <summary>Сценарий записи: «потраченное» время, фрагменты и длительность записи, которую сообщит распознаватель.</summary>
    private sealed record Script(TimeSpan Cost, TranscriptSegmentDraft[] Segments, long? DurationMs = null);

    /// <summary>Распознаватель по сценарию: по имени файла отдаёт фрагменты и «тратит» время; неизвестный файл — сбой.</summary>
    private sealed class ScriptedTranscriber(ManualClock clock, Action? onStart = null) : IAudioTranscriber
    {
        public const string Version = "fake-asr@000000000000";

        private readonly Dictionary<string, Script> _scripts = new(StringComparer.OrdinalIgnoreCase);

        public string ModelVersion => Version;

        public Script this[string file]
        {
            set => _scripts[file] = value;
        }

        public async Task<AudioTranscription> TranscribeAsync(string sourcePath, CancellationToken cancellationToken = default)
        {
            onStart?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            if (!_scripts.TryGetValue(Path.GetFileName(sourcePath), out var script))
            {
                throw new InvalidOperationException("сбой модели");
            }

            clock.Advance(script.Cost);
            await Task.Yield();
            return new AudioTranscription(script.Segments, script.DurationMs);
        }
    }

    private sealed class FixedDurations : Dictionary<string, TimeSpan>, IAudioDurationProbe
    {
        public Task<TimeSpan?> GetDurationAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult(TryGetValue(Path.GetFileName(path), out var duration) ? duration : (TimeSpan?)null);
    }

    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
