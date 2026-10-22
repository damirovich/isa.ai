using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Speech;
using ISC.AI.Speech.Protocol;
using ISC.AI.Speech.Speakers;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Speech;

/// <summary>
/// Разделение говорящих в адаптере (ТФ-МЕД-19, ADR-0062) без моделей и процесса: строки реплик протокола, приписывание
/// дикторов фрагментам по перекрытию, разметка стерео-записи по каналам на синтетическом звуке, настройки секции
/// <c>Speech:Speakers</c> и аргументы процесса-распознавателя.
/// </summary>
public sealed class SpeakerDiarizationTests
{
    private const int Rate = 8_000;

    [Fact(DisplayName = "Протокол: строка turn — реплика с таймкодами и номером диктора; done без полей разметки — как прежде")]
    public void Turn_line_is_parsed()
    {
        SpeechWorkerProtocol.ParseLine("""{"type":"turn","startMs":1200,"endMs":3400,"speaker":2}""")
            .ShouldBe(new SpeechWorkerTurn(1_200, 3_400, 2));

        var done = SpeechWorkerProtocol.ParseLine("""{"type":"done","durationMs":5000,"segments":0}""").ShouldBeOfType<SpeechWorkerDone>();
        done.Diarization.ShouldBeNull();
        done.DiarizationNote.ShouldBeNull();

        var failed = SpeechWorkerProtocol.ParseLine(
            """{"type":"done","durationMs":5000,"segments":0,"diarization":"failed","diarizationNote":"сбой разметки дикторов"}""")
            .ShouldBeOfType<SpeechWorkerDone>();
        (failed.Diarization, failed.DiarizationNote).ShouldBe(("failed", "сбой разметки дикторов"));
    }

    [Fact(DisplayName = "Вывод: реплики копятся отдельно от фрагментов и не ломают проверку номеров фрагментов")]
    public void Parser_collects_turns()
    {
        var parser = new SpeechWorkerOutputParser();
        var segments = new[]
            {
                """{"type":"segment","index":0,"startMs":0,"endMs":800,"text":"раз"}""",
                """{"type":"turn","startMs":0,"endMs":900,"speaker":0}""",
                """{"type":"segment","index":1,"startMs":1000,"endMs":1800,"text":"два"}""",
                """{"type":"turn","startMs":900,"endMs":2000,"speaker":1}""",
                """{"type":"done","durationMs":2000,"segments":2,"diarization":"done"}""",
            }
            .Select(parser.Accept)
            .OfType<TranscriptSegmentDraft>()
            .ToList();

        parser.EnsureCompleted();
        segments.Select(s => s.Text).ShouldBe(["раз", "два"]);
        parser.Turns.ShouldBe([new SpeakerTurnSpan(0, 900, 0), new SpeakerTurnSpan(900, 2_000, 1)]);
    }

    [Fact(DisplayName = "ADR-0062: фрагменту — диктор наибольшего перекрытия; номера с 1 по первому появлению; без перекрытия — без диктора")]
    public void Assignment_by_largest_overlap()
    {
        TranscriptSegmentDraft[] segments =
        [
            new(0, 0, 1_000, "первый"),
            new(1, 1_000, 3_000, "второй"),
            new(2, 3_000, 4_000, "третий"),
            new(3, 9_000, 9_500, "в тишине"),
        ];

        // Источник нумерует дикторов «как хочет»: первым заговорил его диктор 5.
        SpeakerTurnSpan[] turns =
        [
            new(0, 1_200, 5),
            new(1_200, 2_600, 0),  // во фрагменте 1: 1 400 мс против 600 мс диктора 5
            new(2_600, 4_000, 5),
        ];

        var result = SpeakerAssignment.Assign(segments, turns);

        result.Select(s => s.Speaker).ShouldBe([1, 2, 1, null]);
        result.Select(s => s.Text).ShouldBe(["первый", "второй", "третий", "в тишине"]);
        SpeakerAssignment.Assign(segments, []).ShouldBeSameAs(segments);
    }

    [Fact(DisplayName = "ADR-0062: равное перекрытие — меньший номер источника (повторный прогон даёт ту же разметку)")]
    public void Assignment_tie_is_deterministic()
    {
        // Первый фрагмент поровну у источников 3 и 1 — выбран 1 («Диктор 1»); источник 3 во втором фрагменте — «Диктор 2».
        var result = SpeakerAssignment.Assign(
            [new TranscriptSegmentDraft(0, 0, 1_000, "пополам"), new TranscriptSegmentDraft(1, 1_500, 2_000, "второй")],
            [new(500, 1_000, 3), new(0, 500, 1), new(1_500, 2_000, 3)]);

        result.Select(s => s.Speaker).ShouldBe([1, 2]);
    }

    [Fact(DisplayName = "Стерео: собеседники в разных каналах — реплики по каналам (левый — 0, правый — 1) с таймкодами")]
    public void Stereo_separate_channels_give_turns()
    {
        // 0–1 с левый, 1–1,5 с тишина, 1,5–3 с правый, 3–4 с левый.
        var wav = StereoWave(
            (1_000, Voice, Quiet),
            (500, Quiet, Quiet),
            (1_500, Quiet, Voice),
            (1_000, Voice, Quiet));

        var turns = StereoChannelAnalyzer.Analyze(new MemoryStream(wav)).ShouldNotBeNull();

        turns.ShouldBe([new SpeakerTurnSpan(0, 1_000, 0), new SpeakerTurnSpan(1_500, 3_000, 1), new SpeakerTurnSpan(3_000, 4_000, 0)]);
    }

    [Fact(DisplayName = "Стерео: каналы-копии (моно, записанное в два канала) — не раздельные, разметка уходит модели")]
    public void Stereo_duplicate_channels_give_null()
    {
        var wav = StereoWave((3_000, Voice, Voice));

        StereoChannelAnalyzer.Analyze(new MemoryStream(wav)).ShouldBeNull();
    }

    [Fact(DisplayName = "Стерео: говорит один (второй канал молчит всю запись) или полная тишина — разделять нечего")]
    public void Stereo_single_speaker_or_silence_give_null()
    {
        StereoChannelAnalyzer.Analyze(new MemoryStream(StereoWave((3_000, Voice, Quiet)))).ShouldBeNull();
        StereoChannelAnalyzer.Analyze(new MemoryStream(StereoWave((3_000, Quiet, Quiet)))).ShouldBeNull();
    }

    [Fact(DisplayName = "Стерео: не 2 канала по 16 бит — InvalidDataException (адаптер тогда отдаёт разметку модели)")]
    public void Stereo_rejects_other_formats()
    {
        Should.Throw<InvalidDataException>(() => StereoChannelAnalyzer.Analyze(new MemoryStream(Wave(channels: 1, [0, 0]))))
            .Message.ShouldContain("каналов 1");
        Should.Throw<InvalidDataException>(() => StereoChannelAnalyzer.Analyze(new MemoryStream(Encoding.ASCII.GetBytes("не wav-файл"))));
    }

    [Fact(DisplayName = "Стерео: короткие разрывы реплики склеиваются, щелчки короче 200 мс отбрасываются")]
    public void Stereo_turns_bridge_gaps_and_drop_clicks()
    {
        // Окна по 100 мс: левый 3 окна, тишина 2 (≤ 300 мс — склейка), левый 2; правый 1 окно (щелчок); левый 5.
        int[] windows = [0, 0, 0, -1, -1, 0, 0, 1, 0, 0, 0, 0, 0];

        StereoChannelAnalyzer.Turns(windows).ShouldBe([new SpeakerTurnSpan(0, 700, 0), new SpeakerTurnSpan(800, 1_300, 0)]);
    }

    [Fact(DisplayName = "Настройки: Speech:Speakers читаются; без путей моделей разметки моделью нет; числа — в границах")]
    public void Speaker_options_are_read_and_bound()
    {
        var empty = SpeechServiceCollectionExtensions.ReadOptions(Configuration(new Dictionary<string, string?>())).Speakers.ShouldNotBeNull();
        empty.ModelEnabled.ShouldBeFalse();
        (empty.Count, empty.Threshold, empty.MaxMinutes).ShouldBe((0, SpeakerOptions.DefaultThreshold, SpeakerOptions.DefaultMaxMinutes));

        var full = SpeechServiceCollectionExtensions.ReadOptions(Configuration(new Dictionary<string, string?>
        {
            ["Speech:Speakers:Segmentation:Path"] = " seg.onnx ",
            ["Speech:Speakers:Segmentation:Sha256"] = "AA",
            ["Speech:Speakers:Embedding:Path"] = "emb.onnx",
            ["Speech:Speakers:Embedding:Sha256"] = "BB",
            ["Speech:Speakers:Count"] = "50",
            ["Speech:Speakers:Threshold"] = "0.7",
            ["Speech:Speakers:MaxMinutes"] = "1000",
        })).Speakers.ShouldNotBeNull();

        full.ModelEnabled.ShouldBeTrue();
        full.SegmentationPath.ShouldBe("seg.onnx");
        (full.Count, full.Threshold, full.MaxMinutes).ShouldBe((SpeakerOptions.MaxCount, 0.7, 360));

        foreach (var key in new[]
                 {
                     "Speech:Speakers:Segmentation:Path", "Speech:Speakers:Segmentation:Sha256", "Speech:Speakers:Embedding:Path",
                     "Speech:Speakers:Embedding:Sha256", "Speech:Speakers:Count", "Speech:Speakers:Threshold", "Speech:Speakers:MaxMinutes",
                 })
        {
            SpeechConfigurationKeys.All.ShouldContain(key);
        }
    }

    [Fact(DisplayName = "Аргументы процесса: модели говорящих — парой с числом, порогом и пределом в инвариантной культуре; без моделей — ключей нет")]
    public void Worker_arguments_with_speakers()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");
        try
        {
            var options = new SpeechOptions("w.exe", "m", "00", "t", "00", "v", "00",
                Speakers: new SpeakerOptions("s", "00", "e", "00", Count: 2, Threshold: 0.75, MaxMinutes: 90));

            var args = SpeechWorkerTranscriber.BuildWorkerArguments(@"C:\m.onnx", @"C:\t.txt", @"C:\v.onnx", @"C:\a.wav", options,
                (@"C:\seg.onnx", @"C:\emb.onnx"));

            args.Skip(12).ShouldBe(
            [
                "--speaker-segmentation", @"C:\seg.onnx", "--speaker-embedding", @"C:\emb.onnx",
                "--speakers", "2", "--speaker-threshold", "0.75", "--speaker-max-minutes", "90",
            ]);

            SpeechWorkerTranscriber.BuildWorkerArguments(@"C:\m.onnx", @"C:\t.txt", @"C:\v.onnx", @"C:\a.wav", options)
                .ShouldNotContain("--speaker-segmentation");

            // v0.2: число, заданное сотрудником, важнее числа из настройки.
            var fixedCount = SpeechWorkerTranscriber.BuildWorkerArguments(@"C:\m.onnx", @"C:\t.txt", @"C:\v.onnx", @"C:\a.wav", options,
                (@"C:\seg.onnx", @"C:\emb.onnx"), speakerCount: 3);
            fixedCount[fixedCount.ToList().IndexOf("--speakers") + 1].ShouldBe("3");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory(DisplayName = "ADR-0062 v0.2: число говорящих вне 1–20 — ошибка программиста до чтения файла")]
    [InlineData(0)]
    [InlineData(21)]
    public async Task Speaker_count_is_bound(int count)
    {
        var transcriber = new SpeechWorkerTranscriber(new SpeechOptions("w.exe", "m", "00", "t", "00", "v", "00"));

        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => transcriber.TranscribeAsync("нет-файла.wav", count));
    }

    private const short Voice = 8_000;
    private const short Quiet = 0;

    // Синтетическая стерео-запись: участки (длительность, амплитуда левого, амплитуда правого) — меандр 400 Гц.
    private static byte[] StereoWave(params (int Ms, short Left, short Right)[] parts)
    {
        var samples = new List<short>();
        foreach (var (ms, left, right) in parts)
        {
            var frames = Rate * ms / 1000;
            for (var i = 0; i < frames; i++)
            {
                var sign = (i / 10) % 2 == 0 ? 1 : -1;
                samples.Add((short)(left * sign));
                samples.Add((short)(right * sign));
            }
        }

        return Wave(channels: 2, [.. samples]);
    }

    private static byte[] Wave(int channels, short[] samples)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        var data = samples.Length * 2;
        writer.Write("RIFF"u8);
        writer.Write(36 + data);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)channels);
        writer.Write(Rate);
        writer.Write(Rate * channels * 2);
        writer.Write((short)(channels * 2));
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(data);
        foreach (var sample in samples)
        {
            writer.Write(sample);
        }

        writer.Flush();
        return stream.ToArray();
    }

    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
