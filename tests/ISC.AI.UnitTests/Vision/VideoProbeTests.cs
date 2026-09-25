using System;
using ISC.AI.Modules.Media.Domain.Model;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Vision;

/// <summary>
/// Арифметика номера кадра (ADR-0028): номер по моменту и частоте, момент запроса для ffmpeg (на полкадра раньше
/// номинального времени), номинальное время кадра и их взаимная согласованность — считаются в одном месте
/// (<see cref="VideoProbe"/>) и для браузерного шага, и для серверной вырезки, и для подписи «кадр № N».
/// </summary>
public sealed class VideoProbeTests
{
    [Theory(DisplayName = "FrameIndexAt: номер кадра — округление t·fps к ближайшему; 0 мс → кадр 0; статический и экземплярный расчёт совпадают")]
    [InlineData(0, 25.0, 0)]
    [InlineData(40, 25.0, 1)]
    [InlineData(1450, 25.0, 36)]  // 36,25 → 36
    [InlineData(1470, 25.0, 37)]  // 36,75 → 37
    [InlineData(1480, 25.0, 37)]
    [InlineData(1000, 29.97, 30)] // 29,97 → 30
    [InlineData(1000, 59.94, 60)]
    [InlineData(3_600_000, 25.0, 90_000)]
    public void FrameIndexAt_rounds_time_times_rate(long timestampMs, double frameRate, long expected)
    {
        VideoProbe.FrameIndexAt(timestampMs, frameRate).ShouldBe(expected);
        new VideoProbe(frameRate, TimeSpan.FromHours(1), 320, 240).FrameIndexAt(timestampMs).ShouldBe(expected);
    }

    [Fact(DisplayName = "SeekTimeFor: кадр 0 → 0; кадр 37 при 25 к/с → 1,46 с (полкадра раньше номинальных 1,48 с)")]
    public void SeekTimeFor_is_half_frame_before_nominal_time()
    {
        VideoProbe.SeekTimeFor(0, 25).ShouldBe(TimeSpan.Zero);
        VideoProbe.SeekTimeFor(37, 25).TotalSeconds.ShouldBe(1.46, tolerance: 1e-9);
        VideoProbe.SeekTimeFor(1, 25).TotalSeconds.ShouldBe(0.02, tolerance: 1e-9);

        // Момент запроса всегда СТРОГО между номинальными временами кадров N−1 и N: ffmpeg отдаёт первый кадр с
        // меткой ≥ момента, то есть N, даже если метка N округлена контейнером вниз на долю миллисекунды.
        foreach (var fps in new[] { 25.0, 29.97, 30.0, 50.0, 59.94, 15.0 })
        {
            for (long n = 1; n <= 1000; n++)
            {
                var seek = VideoProbe.SeekTimeFor(n, fps).TotalSeconds;
                seek.ShouldBeGreaterThan((n - 1) / fps, $"кадр {n} при {fps}");
                seek.ShouldBeLessThan(n / fps, $"кадр {n} при {fps}");
            }
        }
    }

    [Fact(DisplayName = "TimestampOf: номинальное время кадра в мс — округление N·1000/fps; кадр 0 → 0")]
    public void TimestampOf_rounds_nominal_time()
    {
        VideoProbe.TimestampOf(0, 25).ShouldBe(0);
        VideoProbe.TimestampOf(37, 25).ShouldBe(1480);
        VideoProbe.TimestampOf(1, 29.97).ShouldBe(33);   // 33,367 мс
        VideoProbe.TimestampOf(30, 29.97).ShouldBe(1001); // 1001,0 мс
    }

    [Theory(DisplayName = "Обратимость: FrameIndexAt(TimestampOf(N)) == N для 25, 29,97, 30, 50, 59,94, 15 к/с на N = 0..100000")]
    [InlineData(25.0)]
    [InlineData(29.97)]
    [InlineData(30.0)]
    [InlineData(50.0)]
    [InlineData(59.94)]
    [InlineData(15.0)]
    public void Timestamp_and_frame_index_round_trip(double frameRate)
    {
        // Округление до миллисекунды даёт ошибку ≤ 0,5 мс, то есть ≤ 0,03 кадра при 59,94 к/с — номер не сдвигается.
        for (long n = 0; n <= 100_000; n++)
        {
            var timestamp = VideoProbe.TimestampOf(n, frameRate);
            VideoProbe.FrameIndexAt(timestamp, frameRate).ShouldBe(n, $"кадр {n} при {frameRate} к/с (t = {timestamp} мс)");
        }
    }

    [Fact(DisplayName = "Отрицательные момент/номер кадра и неположительная частота отвергаются; DurationMs — округление длительности, неизвестная или нулевая → null")]
    public void Arguments_are_validated()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => VideoProbe.FrameIndexAt(-1, 25));
        Should.Throw<ArgumentOutOfRangeException>(() => VideoProbe.FrameIndexAt(0, 0));
        Should.Throw<ArgumentOutOfRangeException>(() => VideoProbe.SeekTimeFor(-1, 25));
        Should.Throw<ArgumentOutOfRangeException>(() => VideoProbe.SeekTimeFor(0, -25));
        Should.Throw<ArgumentOutOfRangeException>(() => VideoProbe.TimestampOf(-1, 25));
        Should.Throw<ArgumentOutOfRangeException>(() => VideoProbe.TimestampOf(0, 0));

        new VideoProbe(25, TimeSpan.FromMilliseconds(1999.6), 320, 240).DurationMs.ShouldBe(2000L);

        // Незавершённая запись: длительности нет — и в миллисекундах её нет; ноль длительностью не считается
        // (записанный ноль запер бы просмотр всех кадров t > 0).
        new VideoProbe(25, null, 320, 240).DurationMs.ShouldBeNull();
        new VideoProbe(25, TimeSpan.Zero, 320, 240).DurationMs.ShouldBeNull();
    }
}
