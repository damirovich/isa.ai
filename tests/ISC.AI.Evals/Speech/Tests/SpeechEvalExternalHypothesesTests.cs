using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ISC.AI.Evals.Speech;
using Shouldly;
using Xunit;

namespace ISC.AI.Evals.Speech.Tests;

/// <summary>
/// Готовый выход сторонних моделей (методика пилота, 7.3): тексты <c>hyp/&lt;модель&gt;/&lt;имя&gt;.txt</c> считаются
/// тем же инструментом, что и GigaAM. Записи — синтетическая тишина в WAV, «выход моделей» — выдуманные
/// фразы; реальных записей людей нет (ТО-прог-13).
/// </summary>
public sealed class SpeechEvalExternalHypothesesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "iscai-speech-eval-tests", Guid.NewGuid().ToString("N"));

    public SpeechEvalExternalHypothesesTests() => Directory.CreateDirectory(Set);

    private string Set => Path.Combine(_root, "set");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact(DisplayName = "Готовый выход: тексты по имени записи (и по имени whisper.cpp «rec.ogg.txt»), пропуски и лишние файлы — в замечаниях")]
    public async Task Loads_texts_and_reports_missing_and_extra_files()
    {
        WriteManifest("ru.wav;ru;interview", "calls/ky.ogg;ky;phone", "mixed.wav;mixed;voice");
        WriteRecording("ru.wav", "Проверка связи.");
        WriteRecording("calls/ky.ogg", "Үйдө бала жок.");
        WriteRecording("mixed.wav", "мен бардым");
        WriteText("hyp/whisper/ru.txt", "Проверка связи.");
        WriteText("hyp/whisper/calls/ky.ogg.txt", "уйдо бала жок");
        WriteText("hyp/whisper/typo.txt", "лишний файл");
        var dataset = await SpeechEvalDataset.LoadAsync(Set);

        var hypotheses = await SpeechEvalExternalHypotheses.LoadAsync(dataset, "whisper");

        hypotheses.Model.ShouldBe("whisper");
        hypotheses.Texts.Keys.Order(StringComparer.Ordinal).ShouldBe(["calls/ky.ogg", "ru.wav"]);
        hypotheses.Texts["calls/ky.ogg"].ShouldBe("уйдо бала жок");
        hypotheses.Warnings.Count.ShouldBe(2);
        hypotheses.Warnings[0].ShouldContain("нет готового выхода для 1 записей");
        hypotheses.Warnings[0].ShouldContain("mixed.wav");
        hypotheses.Warnings[1].ShouldContain("typo.txt");
    }

    [Fact(DisplayName = "Готовый выход: нет папки, два файла на запись, не UTF-8, ни одного подходящего файла, негодное имя модели — понятные ошибки")]
    public async Task Reports_problems()
    {
        WriteManifest("a.wav;ru;phone", "b.wav;ru;phone");
        WriteRecording("a.wav", "да");
        WriteRecording("b.wav", "нет");
        var dataset = await SpeechEvalDataset.LoadAsync(Set);

        (await Should.ThrowAsync<SpeechEvalDatasetException>(() => SpeechEvalExternalHypotheses.LoadAsync(dataset, "vosk")))
            .Problems.ShouldHaveSingleItem().ShouldContain("нет папки");

        WriteText("hyp/vosk/a.txt", "да");
        WriteText("hyp/vosk/a.wav.txt", "да");
        WriteBytes("hyp/vosk/b.txt", [0xC4, 0xE0]); // «Да» в Windows-1251 — не UTF-8
        var problems = (await Should.ThrowAsync<SpeechEvalDatasetException>(() => SpeechEvalExternalHypotheses.LoadAsync(dataset, "vosk"))).Problems;
        problems.Count.ShouldBe(2);
        problems[0].ShouldContain("два файла");
        problems[1].ShouldContain("UTF-8");

        Directory.CreateDirectory(Path.Combine(Set, "hyp", "empty"));
        (await Should.ThrowAsync<SpeechEvalDatasetException>(() => SpeechEvalExternalHypotheses.LoadAsync(dataset, "empty")))
            .Problems.ShouldHaveSingleItem().ShouldContain("нет ни одного файла");

        await Should.ThrowAsync<ArgumentException>(() => SpeechEvalExternalHypotheses.LoadAsync(dataset, "../vosk"));
    }

    [Fact(DisplayName = "Готовый выход: строка файла — фрагмент, пустые строки пропускаются, таймкодов нет")]
    public void Lines_become_segments()
    {
        var segments = SpeechEvalExternalHypotheses.ToSegments("  первая строка \r\n\r\nвторая\n");

        segments.Select(segment => segment.Text).ShouldBe(["первая строка", "вторая"]);
        segments.Select(segment => segment.Index).ShouldBe([0, 1]);
        segments.ShouldAllBe(segment => segment.StartMs == 0 && segment.EndMs == 0);
        SpeechEvalExternalHypotheses.ToSegments("").ShouldBeEmpty();
    }

    [Fact(DisplayName = "Прогон по готовому выходу: та же нормализация и [неразборчиво], RTF не считается, контрольный файл — ложные слова")]
    public async Task Runner_scores_precomputed_hypotheses()
    {
        WriteManifest("ky.wav;ky;phone", "music.wav;-;other", "ru.wav;ru;interview");
        WriteRecording("ky.wav", "Үйдө [неразборчиво] жок.", seconds: 2);
        WriteRecording("music.wav", reference: null, seconds: 60);
        WriteRecording("ru.wav", "да", seconds: 1);
        WriteText("hyp/vosk-ky/ky.txt", "уйдо бала\nжок");
        WriteText("hyp/vosk-ky/music.txt", "субтитры сделал\n\nкто-то\n");
        var dataset = await SpeechEvalDataset.LoadAsync(Set);
        var hypotheses = await SpeechEvalExternalHypotheses.LoadAsync(dataset, "vosk-ky");

        var report = await new SpeechEvalRunner(hypotheses, new AudioDurationProbe(useFfprobe: false)).RunAsync(dataset);

        report.ModelVersion.ShouldBe("vosk-ky");
        report.HypothesisFolder.ShouldBe(Path.Combine(Set, "hyp", "vosk-ky"));
        report.Records.Select(record => record.Recording.File).ShouldBe(["ky.wav", "music.wav"]); // ru.wav — нет выхода
        report.DatasetWarnings.ShouldContain(warning => warning.Contains("ru.wav"));

        var kyrgyz = report.Records[0];
        kyrgyz.Precomputed.ShouldBeTrue();
        kyrgyz.Segments.Count.ShouldBe(2);
        kyrgyz.Hypothesis.ShouldBe("уйдо бала жок");
        kyrgyz.Words.Substitutions.ShouldBe(1); // «уйдо» вместо «үйдө»
        kyrgyz.Words.Unscored.ShouldBe(1); // «бала» на месте [неразборчиво]
        kyrgyz.WordErrorRate!.Value.ShouldBe(0.5, 1e-12);
        kyrgyz.AudioDuration.ShouldBe(TimeSpan.FromSeconds(2));
        kyrgyz.RealTimeFactor.ShouldBeNull();

        var group = SpeechEvalAggregator.ByLanguage(report.Records).Single(item => item.Key == "ky");
        group.Elapsed.ShouldBeNull();
        group.RealTimeFactor.ShouldBeNull();
        group.Audio.ShouldBe(TimeSpan.FromSeconds(2)); // длительность звука видна и без замера времени
        group.TimedAudio.ShouldBe(TimeSpan.Zero);

        var noSpeech = SpeechEvalAggregator.NoSpeech(report.Records).ShouldNotBeNull();
        noSpeech.FalseWords.ShouldBe(4); // «субтитры сделал кто то»
        noSpeech.FalseFragments.ShouldBe(2);
        noSpeech.FalseFragmentsPer10Minutes!.Value.ShouldBe(20.0, 1e-9); // 2 фрагмента на 1 минуту
    }

    [Fact(DisplayName = "Пилот по готовому выходу одной командой: отчёт с пометкой источника, расшифровка без таймкодов, строка no_speech")]
    public async Task Pilot_runs_precomputed_end_to_end()
    {
        WriteManifest("файл;язык;условия;примечание", "ru.wav;ru;dictaphone;", "silence.wav;-;dictaphone;тишина");
        WriteRecording("ru.wav", "Проверка связи.", seconds: 2);
        WriteRecording("silence.wav", reference: null, seconds: 3);
        WriteText("hyp/whisper-turbo/ru.txt", "Проверка связи.");
        WriteText("hyp/whisper-turbo/silence.txt", "Продолжение следует...");
        var output = Path.Combine(_root, "out");

        var report = await SpeechEvalPilot.RunPrecomputedAndExportAsync(Set, "whisper-turbo", output, ffprobeFolder: Path.Combine(_root, "no-ffprobe"));

        report.Records.Count.ShouldBe(2);
        report.Records[0].WordErrorRate.ShouldBe(0.0);
        var markdown = await File.ReadAllTextAsync(Path.Combine(output, "report.md"));
        markdown.ShouldContain("готовый выход модели");
        markdown.ShouldContain("## Контрольные файлы без речи");
        markdown.ShouldContain("ложных слов 2, ложных фрагментов 1");
        var transcript = await File.ReadAllTextAsync(Path.Combine(output, "transcripts", "silence.hyp.txt"));
        transcript.ShouldContain("таймкодов нет");
        transcript.ShouldContain("\nПродолжение следует...");
        transcript.ShouldNotContain("[00:");
        var summary = await File.ReadAllTextAsync(Path.Combine(output, "summary.csv"));
        summary.ShouldContain("whisper-turbo;no_speech;-;1;0;");

        await Should.ThrowAsync<ArgumentException>(
            () => SpeechEvalPilot.RunPrecomputedAndExportAsync(Set, "whisper-turbo", Path.Combine(Set, "out")));
    }

    private void WriteManifest(params string[] lines) =>
        File.WriteAllText(Path.Combine(Set, SpeechEvalManifest.FileName), string.Join("\r\n", lines), new UTF8Encoding(true));

    // Запись — синтетическая тишина (WAV 16 кГц, моно, 16 бит) или просто байт для не-WAV; эталон рядом.
    private void WriteRecording(string relativePath, string? reference, int seconds = 1)
    {
        var path = FullPath(relativePath);
        File.WriteAllBytes(path, string.Equals(Path.GetExtension(path), ".wav", StringComparison.OrdinalIgnoreCase) ? SilentWav(seconds) : [0]);
        if (reference is not null)
        {
            WriteText(Path.ChangeExtension(relativePath, ".txt"), reference);
        }
    }

    private void WriteText(string relativePath, string text) =>
        File.WriteAllText(FullPath(relativePath), text, new UTF8Encoding(false));

    private void WriteBytes(string relativePath, byte[] bytes) =>
        File.WriteAllBytes(FullPath(relativePath), bytes);

    private string FullPath(string relativePath)
    {
        var path = Path.Combine(Set, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    private static byte[] SilentWav(int seconds)
    {
        const int sampleRate = 16_000;
        var dataBytes = sampleRate * 2 * seconds;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataBytes);
        writer.Write(new byte[dataBytes]);
        writer.Flush();
        return stream.ToArray();
    }
}
