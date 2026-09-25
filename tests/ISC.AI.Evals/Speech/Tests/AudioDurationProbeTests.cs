using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using ISC.AI.Evals.Speech;
using Shouldly;
using Xunit;

namespace ISC.AI.Evals.Speech.Tests;

/// <summary>Длительность звука для RTF: заголовок WAV и разбор вывода ffprobe (без запуска ffprobe).</summary>
public sealed class AudioDurationProbeTests
{
    [Fact(DisplayName = "WAV: 16 кГц моно 16 бит, 32 000 байт данных — ровно 1 секунда")]
    public void Reads_duration_from_wav_header()
    {
        var wav = BuildWav(sampleRate: 16_000, channels: 1, bitsPerSample: 16, dataBytes: 32_000);

        AudioDurationProbe.TryReadWavDuration(wav, wav.Length, out var duration).ShouldBeTrue();
        duration.ShouldBe(TimeSpan.FromSeconds(1));
    }

    [Fact(DisplayName = "WAV: чанк LIST перед данными и нечётный размер чанка не мешают")]
    public void Skips_extra_chunks()
    {
        var wav = BuildWav(sampleRate: 8_000, channels: 2, bitsPerSample: 16, dataBytes: 64_000, extraChunkBytes: 5);

        AudioDurationProbe.TryReadWavDuration(wav, wav.Length, out var duration).ShouldBeTrue();
        duration.ShouldBe(TimeSpan.FromSeconds(2)); // 8000 × 2 канала × 2 байта = 32 000 байт/с
    }

    [Fact(DisplayName = "WAV: размер данных 0xFFFFFFFF (потоковая запись) — берётся остаток файла")]
    public void Uses_file_length_for_streaming_wav()
    {
        var wav = BuildWav(sampleRate: 16_000, channels: 1, bitsPerSample: 16, dataBytes: 16_000);
        BinaryPrimitives.WriteUInt32LittleEndian(wav.AsSpan(40, 4), uint.MaxValue);

        AudioDurationProbe.TryReadWavDuration(wav, wav.Length, out var duration).ShouldBeTrue();
        duration.ShouldBe(TimeSpan.FromSeconds(0.5));
    }

    [Fact(DisplayName = "Не WAV (или обрезанный заголовок) — длительность не определена")]
    public void Rejects_non_wav()
    {
        var notWav = Encoding.ASCII.GetBytes("OggS\0\0\0\0\0\0\0\0\0\0\0\0");

        AudioDurationProbe.TryReadWavDuration(notWav, notWav.Length, out _).ShouldBeFalse();
        AudioDurationProbe.TryReadWavDuration(Encoding.ASCII.GetBytes("RIFF"), 4, out _).ShouldBeFalse();
    }

    [Fact(DisplayName = "ffprobe: «83.450000» — 83,45 с; N/A, пусто и мусор — неудача")]
    public void Parses_ffprobe_output()
    {
        AudioDurationProbe.TryParseFfprobeDuration("83.450000\r\n", out var duration).ShouldBeTrue();
        duration.ShouldBe(TimeSpan.FromSeconds(83.45));

        AudioDurationProbe.TryParseFfprobeDuration("N/A", out _).ShouldBeFalse();
        AudioDurationProbe.TryParseFfprobeDuration("", out _).ShouldBeFalse();
        AudioDurationProbe.TryParseFfprobeDuration(null, out _).ShouldBeFalse();
        AudioDurationProbe.TryParseFfprobeDuration("-1", out _).ShouldBeFalse();
    }

    [Fact(DisplayName = "Файл WAV на диске читается без ffprobe; не-WAV без ffprobe — null, а не исключение")]
    public async Task Probe_reads_wav_file_and_tolerates_missing_ffprobe()
    {
        var folder = Path.Combine(Path.GetTempPath(), "iscai-speech-eval-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var wavPath = Path.Combine(folder, "silence.wav");
            await File.WriteAllBytesAsync(wavPath, BuildWav(16_000, 1, 16, 48_000));
            var oggPath = Path.Combine(folder, "voice.ogg");
            await File.WriteAllBytesAsync(oggPath, [1, 2, 3]);

            var probe = new AudioDurationProbe(useFfprobe: false);

            (await probe.GetDurationAsync(wavPath)).ShouldBe(TimeSpan.FromSeconds(1.5));
            (await probe.GetDurationAsync(oggPath)).ShouldBeNull();
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    // Синтетический WAV (тишина): заголовок RIFF/WAVE, fmt, необязательный LIST, data.
    private static byte[] BuildWav(int sampleRate, short channels, short bitsPerSample, int dataBytes, int extraChunkBytes = 0)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        var blockAlign = (short)(channels * bitsPerSample / 8);

        writer.Write("RIFF"u8);
        writer.Write(0); // размер RIFF для разбора не нужен
        writer.Write("WAVE"u8);

        if (extraChunkBytes > 0)
        {
            writer.Write("LIST"u8);
            writer.Write(extraChunkBytes);
            writer.Write(new byte[extraChunkBytes + (extraChunkBytes & 1)]);
        }

        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1); // PCM
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * blockAlign);
        writer.Write(blockAlign);
        writer.Write(bitsPerSample);

        writer.Write("data"u8);
        writer.Write(dataBytes);
        writer.Write(new byte[dataBytes]);
        writer.Flush();
        return stream.ToArray();
    }
}
