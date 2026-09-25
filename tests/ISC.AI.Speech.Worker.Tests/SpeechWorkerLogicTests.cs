using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Speech.Protocol;
using Shouldly;
using Xunit;

namespace ISC.AI.Speech.Worker.Tests;

/// <summary>
/// Чистая логика процесса-распознавателя <c>ISC.AI.Speech.Worker</c> (ADR-0026) без sherpa-onnx и моделей:
/// разбор аргументов и коды выхода, чтение WAV, нарезка длинных участков речи, запись протокола — и её
/// совместимость с разбором на стороне адаптера.
/// </summary>
public sealed class SpeechWorkerLogicTests
{
    private const int Rate = 16000;

    // ---- Аргументы ----

    [Fact(DisplayName = "Аргументы: обязательные пути и умолчания (4 потока, куски до 20 с, 64 мел-полосы)")]
    public void Arguments_are_parsed_with_defaults()
    {
        var args = WorkerArguments.Parse(["--model", "m.onnx", "--tokens", "t.txt", "--vad", "v.onnx", "--input", "a.wav"]);

        args.ShouldBe(new WorkerArguments("m.onnx", "t.txt", "v.onnx", "a.wav", 4, 20, 64));
    }

    [Fact(DisplayName = "Аргументы: числа — в инвариантной записи (точка), в границах")]
    public void Arguments_parse_numbers()
    {
        var args = WorkerArguments.Parse(
        [
            "--input", "a.wav", "--vad", "v.onnx", "--tokens", "t.txt", "--model", "m.onnx",
            "--threads", "8", "--max-segment-seconds", "12.5", "--feature-dim", "80",
        ]);

        args.Threads.ShouldBe(8);
        args.MaxSegmentSeconds.ShouldBe(12.5);
        args.FeatureDim.ShouldBe(80);
    }

    [Theory(DisplayName = "Аргументы: опечатки и пропуски — явная ошибка, а не молчаливое умолчание")]
    [InlineData("не задан обязательный аргумент --vad", "--model", "m", "--tokens", "t", "--input", "a")]
    [InlineData("Неизвестный аргумент «--thread»", "--model", "m", "--tokens", "t", "--vad", "v", "--input", "a", "--thread", "4")]
    [InlineData("задан дважды", "--model", "m", "--model", "m2", "--tokens", "t", "--vad", "v", "--input", "a")]
    [InlineData("не задано значение", "--model", "--tokens", "t", "--vad", "v", "--input", "a")]
    [InlineData("ожидается целое от 1 до 64", "--model", "m", "--tokens", "t", "--vad", "v", "--input", "a", "--threads", "0")]
    [InlineData("разделитель — точка", "--model", "m", "--tokens", "t", "--vad", "v", "--input", "a", "--max-segment-seconds", "12,5")]
    [InlineData("ожидается число от 2 до 30", "--model", "m", "--tokens", "t", "--vad", "v", "--input", "a", "--max-segment-seconds", "45")]
    public void Wrong_arguments_are_explicit(string expected, params string[] args) =>
        Should.Throw<WorkerUsageException>(() => WorkerArguments.Parse(args))
            .Message.ShouldContain(expected, Case.Insensitive);

    [Fact(DisplayName = "Аргументы: диагностический --decode-whole — переключатель без значения в любом месте строки; по умолчанию выключен")]
    public void Decode_whole_switch_is_parsed()
    {
        WorkerArguments.Parse(["--model", "m", "--tokens", "t", "--vad", "v", "--input", "a"]).DecodeWhole.ShouldBeFalse();

        var args = WorkerArguments.Parse(["--decode-whole", "--model", "m", "--tokens", "t", "--vad", "v", "--input", "a", "--threads", "2"]);

        args.ShouldBe(new WorkerArguments("m", "t", "v", "a", Threads: 2, DecodeWhole: true));
        WorkerArguments.Parse(["--model", "m", "--tokens", "t", "--vad", "v", "--input", "a", "--decode-whole"]).DecodeWhole.ShouldBeTrue();
    }

    [Theory(DisplayName = "Аргументы: --decode-whole дважды или со значением — явная ошибка")]
    [InlineData("задан дважды", "--decode-whole", "--model", "m", "--tokens", "t", "--vad", "v", "--input", "a", "--decode-whole")]
    [InlineData("Неизвестный аргумент «yes»", "--model", "m", "--tokens", "t", "--vad", "v", "--input", "a", "--decode-whole", "yes")]
    [InlineData("не задано значение", "--model", "--decode-whole", "--tokens", "t", "--vad", "v", "--input", "a")]
    public void Decode_whole_misuse_is_explicit(string expected, params string[] args) =>
        Should.Throw<WorkerUsageException>(() => WorkerArguments.Parse(args))
            .Message.ShouldContain(expected, Case.Insensitive);

    [Fact(DisplayName = "Справка называет --decode-whole диагностическим ключом")]
    public void Usage_describes_decode_whole_as_diagnostic()
    {
        WorkerArguments.Usage.ShouldContain("[--decode-whole]");
        WorkerArguments.Usage.ShouldContain("диагностика", Case.Insensitive);
    }

    [Fact(DisplayName = "Аргументы: отсутствующие файлы перечисляются с полными путями")]
    public void Missing_files_are_listed_with_full_paths()
    {
        var args = new WorkerArguments("m.onnx", "t.txt", "v.onnx", "a.wav");

        var missing = args.FindMissingFiles(path => path == "t.txt");

        missing.Count.ShouldBe(3);
        missing[0].ShouldStartWith("--model");
        missing[0].ShouldContain(Path.GetFullPath("m.onnx"));
        missing.ShouldNotContain(line => line.StartsWith("--tokens", StringComparison.Ordinal));
    }

    [Fact(DisplayName = "Код выхода 2: неверные аргументы и отсутствующие файлы — до загрузки sherpa-onnx; stdout чист")]
    public void Bad_arguments_exit_with_code_2_and_clean_stdout()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        WorkerApp.Run(["--model"], stdout, stderr).ShouldBe(WorkerExitCodes.InvalidArguments);
        stderr.ToString().ShouldContain("ISC.AI.Speech.Worker --model");

        var absent = Path.Combine(Path.GetTempPath(), "нет-" + Guid.NewGuid().ToString("N"));
        WorkerApp.Run(["--model", absent, "--tokens", absent, "--vad", absent, "--input", absent], stdout, stderr)
            .ShouldBe(WorkerExitCodes.InvalidArguments);
        stderr.ToString().ShouldContain("файл не найден");

        // stdout — только протокол; при отказе в нём нет ничего (адаптер не примет мусор за фрагменты).
        stdout.ToString().ShouldBeEmpty();
    }

    // ---- WAV ----

    [Fact(DisplayName = "WAV: PCM 16 бит моно 16 кГц читается порциями в [-1, 1); служебные блоки пропускаются")]
    public void Wave_reader_reads_pcm16_mono()
    {
        short[] samples = [0, 16384, -32768, 32767, -1, 100, 200];
        using var reader = Pcm16WaveReader.Open(new MemoryStream(Wave(samples, extraChunk: true)));

        var buffer = new float[4];
        var all = new List<float>();
        int read;
        while ((read = reader.Read(buffer)) > 0)
        {
            all.AddRange(buffer.Take(read));
        }

        all.Count.ShouldBe(samples.Length);
        all[1].ShouldBe(0.5f);
        all[2].ShouldBe(-1f);
        all[3].ShouldBeLessThan(1f);
        reader.SamplesRead.ShouldBe(samples.Length);
    }

    [Fact(DisplayName = "WAV: WAVE_FORMAT_EXTENSIBLE с подформатом PCM принимается")]
    public void Wave_reader_accepts_extensible_pcm()
    {
        using var reader = Pcm16WaveReader.Open(new MemoryStream(Wave([1, 2, 3], extensible: true)));

        reader.Read(new float[10]).ShouldBe(3);
    }

    [Fact(DisplayName = "WAV: длина данных 0 (запись в поток) — читается до конца файла; оборванный последний отсчёт отбрасывается")]
    public void Wave_reader_handles_unknown_length_and_truncation()
    {
        var bytes = Wave([10, 20, 30], dataSizeOverride: 0).Concat(new byte[] { 0x7F }).ToArray();
        using var reader = Pcm16WaveReader.Open(new MemoryStream(bytes));

        reader.Read(new float[10]).ShouldBe(3);
        reader.Read(new float[10]).ShouldBe(0);
    }

    [Fact(DisplayName = "WAV: длина записи известна до чтения — по заголовку, но не больше того, что есть в файле")]
    public void Wave_reader_knows_expected_length_before_reading()
    {
        using (var declared = Pcm16WaveReader.Open(new MemoryStream(Wave([1, 2, 3, 4, 5]))))
        {
            declared.ExpectedSamples.ShouldBe(5);
        }

        // Заголовок обещает 5 отсчётов, в файле — 3: верить надо файлу.
        var truncated = Wave([1, 2, 3, 4, 5], dataSizeOverride: 10)[..^4];
        using (var reader = Pcm16WaveReader.Open(new MemoryStream(truncated)))
        {
            reader.ExpectedSamples.ShouldBe(3);
        }

        // «До конца файла» (0xFFFFFFFF — так ffmpeg пишет WAV больше 4 ГБ): длина — по размеру файла.
        using (var unknown = Pcm16WaveReader.Open(new MemoryStream(Wave([1, 2, 3, 4], dataSizeOverride: uint.MaxValue))))
        {
            unknown.ExpectedSamples.ShouldBe(4);
        }

        // Поток без перемотки и без длины в заголовке: заранее неизвестно — проверка останется за чтением.
        using var stream = new NonSeekableStream(Wave([1, 2, 3], dataSizeOverride: 0));
        using var endless = Pcm16WaveReader.Open(stream);
        endless.ExpectedSamples.ShouldBeNull();
        endless.Read(new float[10]).ShouldBe(3);
    }

    [Theory(DisplayName = "WAV: другой формат — явный отказ с описанием (частота, каналы, разрядность)")]
    [InlineData(8000, 1, 16, "8000 Гц")]
    [InlineData(16000, 2, 16, "каналов 2")]
    [InlineData(16000, 1, 24, "24 бит")]
    public void Wave_reader_rejects_other_formats(int rate, int channels, int bits, string expected) =>
        Should.Throw<InvalidDataException>(() => Pcm16WaveReader.Open(new MemoryStream(Wave([1], rate: rate, channels: channels, bits: bits))))
            .Message.ShouldContain(expected);

    [Fact(DisplayName = "WAV: не WAV вовсе — явный отказ")]
    public void Wave_reader_rejects_non_wave() =>
        Should.Throw<InvalidDataException>(() => Pcm16WaveReader.Open(new MemoryStream(Encoding.ASCII.GetBytes("OggS................"))))
            .Message.ShouldContain("RIFF");

    // ---- Нарезка ----

    [Fact(DisplayName = "Нарезка: участок не длиннее предела — один кусок")]
    public void Short_segment_is_one_piece()
    {
        var pieces = SegmentSplitter.Split(new float[Rate * 5], Rate * 20, Rate * 3, 160);

        pieces.ShouldBe(new[] { new SegmentPiece(0, Rate * 5) });
    }

    [Fact(DisplayName = "Нарезка: длинный участок — куски не длиннее предела, встык, покрывают весь участок")]
    public void Long_segment_is_covered_by_bounded_pieces()
    {
        var random = new Random(7);
        var samples = Enumerable.Range(0, Rate * 61).Select(_ => (float)(random.NextDouble() - 0.5)).ToArray();

        var pieces = SegmentSplitter.Split(samples, Rate * 20, Rate * 3, 160);

        pieces.Count.ShouldBeGreaterThanOrEqualTo(4);
        pieces[0].Offset.ShouldBe(0);
        for (var i = 0; i < pieces.Count; i++)
        {
            pieces[i].Length.ShouldBeInRange(1, Rate * 20);
            if (i > 0)
            {
                pieces[i].Offset.ShouldBe(pieces[i - 1].Offset + pieces[i - 1].Length, "куски идут встык");
            }
        }

        pieces[^1].Offset.ShouldBe(samples.Length - pieces[^1].Length, "покрыт весь участок");
    }

    [Fact(DisplayName = "Нарезка: разрез — в самом тихом месте перед пределом, а не посреди звука")]
    public void Cut_is_made_at_quiet_point()
    {
        // 25 с «речи» (громкий сигнал) с паузой 200 мс на 18-й секунде: при пределе 20 с резать надо в паузе.
        var samples = Enumerable.Range(0, Rate * 25).Select(i => (float)Math.Sin(i * 0.3) * 0.8f).ToArray();
        var pauseStart = Rate * 18;
        Array.Clear(samples, pauseStart, Rate / 5);

        var pieces = SegmentSplitter.Split(samples, Rate * 20, Rate * 3, 160);

        pieces.Count.ShouldBe(2);
        pieces[0].Length.ShouldBeInRange(pauseStart, pauseStart + Rate / 5);
    }

    // ---- Протокол ----

    [Fact(DisplayName = "Протокол утилиты разбирается адаптером: номера подряд, пустой текст не пишется, done сходится; UTF-8 без BOM, кириллица как есть")]
    public void Worker_protocol_round_trips_through_adapter_parser()
    {
        var output = new StringWriter { NewLine = "\n" };
        var writer = new ProtocolWriter(output);

        writer.WriteSegment(1230, 4560, " үйгө бардым ").ShouldBeTrue();
        writer.WriteSegment(5000, 5200, "   ").ShouldBeFalse();
        writer.WriteSegment(6000, 9000, "it's домой").ShouldBeTrue();
        writer.WriteDone(10_000);

        var text = output.ToString();
        text.ShouldContain("үйгө бардым");
        text.ShouldNotStartWith("﻿");
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Length.ShouldBe(3);

        var parser = new SpeechWorkerOutputParser();
        var segments = lines.Select(parser.Accept).Where(s => s is not null).Cast<TranscriptSegmentDraft>().ToList();
        parser.EnsureCompleted();

        segments.ShouldBe(new[]
        {
            new TranscriptSegmentDraft(0, 1230, 4560, "үйгө бардым"),
            new TranscriptSegmentDraft(1, 6000, 9000, "it's домой"),
        });
        parser.Done.ShouldBe(new SpeechWorkerDone(10_000, 2));
    }

    // WAV в памяти: RIFF/WAVE, fmt (обычный или EXTENSIBLE), при необходимости служебный блок LIST нечётной
    // длины (проверка байта-заполнителя), затем data.
    private static byte[] Wave(
        short[] samples, int rate = Rate, int channels = 1, int bits = 16,
        bool extensible = false, bool extraChunk = false, uint? dataSizeOverride = null)
    {
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        w.Write("RIFF"u8);
        w.Write(0u);
        w.Write("WAVE"u8);

        if (extraChunk)
        {
            w.Write("LIST"u8);
            w.Write(3u);
            w.Write(new byte[] { 1, 2, 3, 0 }); // 3 байта + заполнитель
        }

        w.Write("fmt "u8);
        w.Write(extensible ? 40u : 16u);
        w.Write(extensible ? (ushort)0xFFFE : (ushort)1);
        w.Write((ushort)channels);
        w.Write(rate);
        w.Write(rate * channels * bits / 8);
        w.Write((ushort)(channels * bits / 8));
        w.Write((ushort)bits);
        if (extensible)
        {
            w.Write((ushort)22);
            w.Write((ushort)bits);
            w.Write(4u);
            w.Write((ushort)1); // подформат PCM: первые байты GUID KSDATAFORMAT_SUBTYPE_PCM
            w.Write(new byte[14]);
        }

        w.Write("data"u8);
        w.Write(dataSizeOverride ?? (uint)(samples.Length * 2));
        foreach (var sample in samples)
        {
            w.Write(sample);
        }

        w.Flush();
        return stream.ToArray();
    }

    // Поток без перемотки (как канал от другого процесса): длина неизвестна.
    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
    }
}
