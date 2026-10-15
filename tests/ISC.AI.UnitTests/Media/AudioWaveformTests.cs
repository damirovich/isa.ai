using System;
using System.Collections.Generic;
using System.Linq;
using ISC.AI.Modules.Media.Domain.Model;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Media;

/// <summary>
/// Построитель волны аудиозаписи (ADR-0047) без ffmpeg: звук s16le моно 8 кГц → наибольший |отсчёт| в окне 20 мс
/// (160 отсчётов) → 0–255 с округлением вверх; поток режется где угодно, в том числе посреди отсчёта; неполное
/// последнее окно — окно; длинная запись укрупняется до 3000 пиков с шагом, кратным 20 мс.
/// </summary>
public sealed class AudioWaveformTests
{
    [Fact(DisplayName = "Окно 20 мс = 160 отсчётов: пик — наибольший модуль (и отрицательный), шкала 0–255 с округлением вверх")]
    public void Builder_takes_peak_magnitude_per_window()
    {
        var builder = new AudioWaveformBuilder();
        var pcm = new List<short>();
        pcm.AddRange(Window(100, extreme: short.MinValue)); // −32768 → полная шкала
        pcm.AddRange(Window(0));                              // тишина
        pcm.AddRange(Window(50, extreme: 64));                // тихо, но не ноль: ⌈64·255/32768⌉ = 1
        builder.Push(Bytes(pcm));

        var waveform = builder.Build();

        AudioWaveformBuilder.SamplesPerWindow.ShouldBe(160);
        waveform.StepMs.ShouldBe(20);
        waveform.Version.ShouldBe(AudioWaveform.CurrentVersion);
        waveform.Peaks.ShouldBe(new byte[] { 255, 0, 1 });
        waveform.DurationMs.ShouldBe(60);
    }

    [Fact(DisplayName = "Поток кусками по 1, 3 и 7 байт (разрыв посреди отсчёта) даёт ту же волну, что одним куском")]
    public void Builder_handles_chunks_split_inside_samples()
    {
        var pcm = Enumerable.Range(0, 1000).Select(i => (short)((i * 37 % 2000) - 1000)).ToList();
        var bytes = Bytes(pcm);
        var whole = new AudioWaveformBuilder();
        whole.Push(bytes);

        foreach (var size in new[] { 1, 3, 7 })
        {
            var chunked = new AudioWaveformBuilder();
            for (var offset = 0; offset < bytes.Length; offset += size)
            {
                chunked.Push(bytes.AsSpan(offset, Math.Min(size, bytes.Length - offset)));
            }

            chunked.Build().Peaks.ShouldBe(whole.Build().Peaks);
        }
    }

    [Fact(DisplayName = "Неполное последнее окно — тоже окно; оборванный последний байт отбрасывается; пустой поток — пустая волна")]
    public void Builder_keeps_partial_window_and_drops_dangling_byte()
    {
        var builder = new AudioWaveformBuilder();
        builder.Push(Bytes(Window(0)));
        builder.Push(Bytes([1000, -2000]));
        builder.Push([0x7F]); // половина отсчёта

        builder.WindowCount.ShouldBe(2);
        builder.Build().Peaks.ShouldBe(new byte[] { 0, AudioWaveformBuilder.Scale(2000) });
        new AudioWaveformBuilder().Build().Peaks.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Длинная запись: окна укрупняются по ⌈n/3000⌉ — не больше 3000 пиков, пик — наибольший в группе, шаг кратен 20 мс")]
    public void Reduce_groups_windows_up_to_max_peaks()
    {
        // 9001 окно (3 мин): по 4 окна в пик — 2251 пик с шагом 80 мс.
        var windows = Enumerable.Repeat((ushort)0, 9001).ToArray();
        windows[5] = 32768;
        windows[9000] = 16384;

        var waveform = AudioWaveformBuilder.Reduce(windows);

        waveform.StepMs.ShouldBe(80);
        waveform.Peaks.Length.ShouldBe(2251);
        waveform.Peaks[1].ShouldBe((byte)255);
        waveform.Peaks[^1].ShouldBe(AudioWaveformBuilder.Scale(16384));
        waveform.Peaks.Count(p => p > 0).ShouldBe(2);

        AudioWaveformBuilder.Reduce(new ushort[3000]).StepMs.ShouldBe(20);
        AudioWaveformBuilder.Reduce(new ushort[3001]).Peaks.Length.ShouldBe(1501);
        AudioWaveformBuilder.Reduce(new ushort[180_000]).Peaks.Length.ShouldBeLessThanOrEqualTo(AudioWaveform.MaxPeaks);
    }

    [Theory(DisplayName = "Шкала: 0 → 0, 1 → 1 (тихий звук не пропадает), половина → 128, полная → 255")]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(16384, 128)]
    [InlineData(32767, 255)]
    [InlineData(32768, 255)]
    public void Scale_rounds_up(int level, byte expected) =>
        AudioWaveformBuilder.Scale(level).ShouldBe(expected);

    private static IEnumerable<short> Window(int level, short? extreme = null) =>
        Enumerable.Range(0, AudioWaveformBuilder.SamplesPerWindow)
            .Select(i => i == 7 && extreme is { } value ? value : (short)(i % 2 == 0 ? level : -level));

    private static byte[] Bytes(IEnumerable<short> samples) =>
        samples.SelectMany(s => new[] { (byte)(s & 0xFF), (byte)((s >> 8) & 0xFF) }).ToArray();
}
