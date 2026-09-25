using System;
using System.Collections.Generic;
using Shouldly;
using Xunit;

namespace ISC.AI.Speech.Worker.Tests;

/// <summary>
/// Чистая арифметика вокруг детектора речи (<see cref="VadSettings"/>, ADR-0026): хвост тишины в конце записи
/// (чтобы речь до последнего отсчёта не терялась), зажатие таймкодов по длине записи, предел длины записи по
/// 32-битным индексам нативного детектора и настройки, от которых зависит отсутствие потерь на стыках.
/// </summary>
public sealed class VadSettingsTests
{
    private const int Window = VadSettings.WindowSize;

    // ---- Хвост тишины ----

    [Theory(DisplayName = "Хвост: неполное последнее окно добивается нулями ровно до целого окна")]
    [InlineData(0L, 0)]
    [InlineData(1L, 511)]
    [InlineData(511L, 1)]
    [InlineData(512L, 0)]
    [InlineData(513L, 511)]
    [InlineData(16_000L, 384)]
    [InlineData(2_000_000_001L, 511)]
    public void Partial_window_is_padded_to_whole_window(long samplesFed, int expected)
    {
        var padding = VadSettings.PartialWindowPadding(samplesFed);

        padding.ShouldBe(expected);
        ((samplesFed + padding) % Window).ShouldBe(0);
    }

    [Fact(DisplayName = "Хвост: тишины подаётся не меньше паузы MinSilenceDuration + 2 окна, целыми окнами")]
    public void Trailing_silence_covers_min_silence_plus_two_windows()
    {
        VadSettings.MinSilenceSamples.ShouldBe((int)(VadSettings.MinSilenceSeconds * VadSettings.SampleRate));
        VadSettings.MinSilenceSamples.ShouldBe(6400);

        var silence = VadSettings.TrailingSilenceWindows * Window;
        silence.ShouldBeGreaterThanOrEqualTo(VadSettings.MinSilenceSamples + 2 * Window);

        // И не бесконечно: хвост — доли секунды, на скорость не влияет.
        silence.ShouldBeLessThanOrEqualTo(VadSettings.SampleRate);
    }

    // ---- Добавка перед участком (pre-roll) ----

    [Fact(DisplayName = "Добавка: участок начинается на 0,25 с раньше, чем его отдал детектор")]
    public void Pre_roll_moves_start_back_by_quarter_second()
    {
        VadSettings.PreRollSamples.ShouldBe(4000);

        VadSettings.PreRollStart(segmentStart: 10_000, previousEnd: 0, historyStart: 0, historyEnd: 20_000).ShouldBe(6_000);
    }

    [Theory(DisplayName = "Добавка: не раньше начала записи, конца предыдущего куска и самого старого отсчёта истории")]
    [InlineData(1_000L, 0L, 0L, 20_000L, 0L)]            // у начала записи — до нуля
    [InlineData(10_000L, 8_000L, 0L, 20_000L, 8_000L)]   // предыдущий кусок кончился 0,125 с назад — только до него
    [InlineData(10_000L, 10_000L, 0L, 20_000L, 10_000L)] // участок встык к предыдущему — без добавки
    [InlineData(10_000L, 12_000L, 0L, 20_000L, 10_000L)] // (невозможное) пересечение — всё равно не позже начала участка
    [InlineData(10_000L, 0L, 9_000L, 20_000L, 9_000L)]   // история помнит лишь часть добавки
    [InlineData(10_000L, 0L, 11_000L, 20_000L, 10_000L)] // история уже забыла звук перед участком — без добавки
    [InlineData(30_000L, 0L, 0L, 20_000L, 30_000L)]      // начало участка дальше истории — без добавки
    public void Pre_roll_respects_bounds(long segmentStart, long previousEnd, long historyStart, long historyEnd, long expected) =>
        VadSettings.PreRollStart(segmentStart, previousEnd, historyStart, historyEnd).ShouldBe(expected);

    [Fact(DisplayName = "Добавка и нарезка: куски подряд идущих участков не перекрываются — двойного звука на стыке нет")]
    public void Pre_roll_with_splitting_never_overlaps()
    {
        const int Rate = VadSettings.SampleRate;
        var random = new Random(3);

        // Участки как от детектора: паузы между ними и длиннее, и короче добавки; два — длиннее предела куска.
        (long Start, int Length)[] segments =
        [
            (1_000, Rate * 3),                     // у начала записи — добавка до нуля
            (Rate * 4 + 1_500, Rate * 2),          // пауза ~1 с — добавка целиком
            (Rate * 6 + 1_500 + 2_000, Rate * 25), // пауза 0,125 с — добавка упрётся в конец предыдущего; 25 с разрежется
            (Rate * 40, Rate * 19 + 15_000),       // 19,9 с, с добавкой длиннее 20 с — тоже разрежется
        ];

        long previousEnd = 0;
        var covered = new List<(long Start, long End)>();
        foreach (var (start, length) in segments)
        {
            var from = VadSettings.PreRollStart(start, previousEnd, historyStart: 0, historyEnd: long.MaxValue);
            from.ShouldBeGreaterThanOrEqualTo(previousEnd, "добавка не заходит в предыдущий кусок");
            from.ShouldBeLessThanOrEqualTo(start);

            var samples = new float[(int)(start - from) + length];
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] = (float)(random.NextDouble() - 0.5);
            }

            foreach (var piece in SegmentSplitter.Split(samples, Rate * 20, Rate * 3, 160))
            {
                covered.Add((from + piece.Offset, from + piece.Offset + piece.Length));
            }

            previousEnd = covered[^1].End;
        }

        for (var i = 1; i < covered.Count; i++)
        {
            covered[i].Start.ShouldBeGreaterThanOrEqualTo(covered[i - 1].End, $"кусок {i} начинается раньше конца куска {i - 1}");
        }

        covered[0].Start.ShouldBe(0, "первый участок получил добавку до начала записи");
        covered.Count.ShouldBeGreaterThan(segments.Length, "длинные участки разрезаны");
    }

    [Fact(DisplayName = "История для добавки: не короче буфера детектора с запасом; память оценена (~39 МБ)")]
    public void History_covers_detector_delay()
    {
        VadSettings.HistorySeconds.ShouldBeGreaterThanOrEqualTo(VadSettings.MaxSpeechSeconds + VadSettings.MinSilenceSeconds + 1);
        VadSettings.HistorySeconds.ShouldBeGreaterThan(VadSettings.BufferSeconds);
        (VadSettings.HistorySamples * (long)sizeof(float)).ShouldBeLessThan(40_000_000L);
    }

    // ---- Зажатие таймкодов ----

    [Fact(DisplayName = "Таймкоды: кусок внутри записи не меняется")]
    public void Piece_inside_recording_is_unchanged() =>
        VadSettings.ClampToRecording(1000, 500, 16_000).ShouldBe((1000L, 1500L));

    [Fact(DisplayName = "Таймкоды: кусок, захвативший добавленную тишину, кончается на последнем настоящем отсчёте")]
    public void Piece_crossing_recording_end_is_clamped() =>
        VadSettings.ClampToRecording(15_800, 1024, 16_000).ShouldBe((15_800L, 16_000L));

    [Theory(DisplayName = "Таймкоды: кусок целиком из добавленной тишины отбрасывается")]
    [InlineData(16_000L, 512)]
    [InlineData(16_300L, 512)]
    [InlineData(100L, 0)]
    public void Piece_of_padding_only_is_dropped(long start, int length) =>
        VadSettings.ClampToRecording(start, length, 16_000).ShouldBeNull();

    // ---- Предел длины записи ----

    [Fact(DisplayName = "Предел длины: граница ровно на MaxInputSamples, с запасом до int.MaxValue на буфер, хвост и окно")]
    public void Index_limit_boundary()
    {
        VadSettings.ExceedsIndexLimit(VadSettings.MaxInputSamples).ShouldBeFalse();
        VadSettings.ExceedsIndexLimit(VadSettings.MaxInputSamples + 1).ShouldBeTrue();
        VadSettings.ExceedsIndexLimit(0).ShouldBeFalse();

        (VadSettings.MaxInputSamples + VadSettings.IndexReserveSamples).ShouldBe(int.MaxValue);

        // Самый дальний номер, который увидит нативный детектор: предел + добивка окна + хвост тишины.
        var farthest = VadSettings.MaxInputSamples + VadSettings.PartialWindowPadding(VadSettings.MaxInputSamples)
            + (long)VadSettings.TrailingSilenceWindows * Window;
        (farthest + (long)(VadSettings.BufferSeconds * VadSettings.SampleRate)).ShouldBeLessThan(int.MaxValue);
    }

    [Fact(DisplayName = "Предел длины: ~37 ч — выше предела адаптера (36 ч), ниже переполнения (2^31 отсчётов ≈ 37,28 ч)")]
    public void Index_limit_is_between_adapter_limit_and_overflow()
    {
        var hours = VadSettings.MaxInputSamples / (double)VadSettings.SampleRate / 3600;

        hours.ShouldBeGreaterThan(36);
        hours.ShouldBeLessThan(int.MaxValue / (double)VadSettings.SampleRate / 3600);
    }

    [Fact(DisplayName = "Предел длины: текст отказа понятен оператору — сколько часов и что делать")]
    public void Too_long_message_is_explicit()
    {
        var message = VadSettings.TooLongMessage(VadSettings.MaxInputSamples + 1);

        message.ShouldContain("Запись длиннее ~37 ч");
        message.ShouldContain("Разбейте запись на части");
        message.ShouldContain((VadSettings.MaxInputSamples + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    // ---- Настройки детектора ----

    [Fact(DisplayName = "Детектор: свой принудительный разрез длинной речи — далеко за пределом куска (режет SegmentSplitter, без потерь)")]
    public void Detector_forced_cut_is_far_beyond_piece_limit()
    {
        // Ноль недопустим: C API sherpa-onnx подставил бы умолчание 20 с — тот самый разрез с потерями.
        VadSettings.MaxSpeechSeconds.ShouldBeGreaterThan(0f);
        VadSettings.MaxSpeechSeconds.ShouldBeGreaterThanOrEqualTo((float)(WorkerArguments.MaxAllowedSegmentSeconds * 10));
    }

    [Fact(DisplayName = "Детектор: буфер вмещает самый длинный участок с запасом и не меньше минуты")]
    public void Detector_buffer_fits_longest_segment() =>
        VadSettings.BufferSeconds.ShouldBe(Math.Max(60f, VadSettings.MaxSpeechSeconds + 10));
}
