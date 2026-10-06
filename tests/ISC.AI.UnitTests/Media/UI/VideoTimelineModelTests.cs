using System;
using System.Linq;
using ISC.AI.Modules.Media.UI;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Media.UI;

/// <summary>
/// Расчёты ленты под видео (ADR-0038, ТФ-МЕД-11): «круглый» шаг делений, проценты положения, опора часов записи
/// (метаданные файла точнее даты съёмки), формат времени записи.
/// </summary>
public sealed class VideoTimelineModelTests
{
    private static readonly TimeZoneInfo Utc6 = TimeZoneInfo.CreateCustomTimeZone("test+6", TimeSpan.FromHours(6), "UTC+6", "UTC+6");

    [Theory(DisplayName = "Шаг делений — наименьший «круглый», при котором делений не больше 10")]
    [InlineData(3_000, 1_000)]
    [InlineData(10_000, 2_000)]
    [InlineData(34_460, 5_000)]
    [InlineData(5 * 60_000, 60_000)]
    [InlineData(60 * 60_000, 600_000)]
    [InlineData(10 * 3_600_000L, 7_200_000)]
    public void Tick_step_is_round(long durationMs, long expected)
    {
        VideoTimelineModel.TickStepMs(durationMs).ShouldBe(expected);
        VideoTimelineModel.Ticks(durationMs).Count.ShouldBeLessThanOrEqualTo(VideoTimelineModel.MaxTicks);
    }

    [Fact(DisplayName = "Деления от нуля, подписи «мм:сс»; у правого края подпись не ставится")]
    public void Ticks_have_labels_except_at_right_edge()
    {
        var ticks = VideoTimelineModel.Ticks(34_460);

        ticks.Select(t => t.Ms).ShouldBe([0, 5_000, 10_000, 15_000, 20_000, 25_000, 30_000]);
        ticks[1].Label.ShouldBe("00:05");

        var edge = VideoTimelineModel.Ticks(10_100); // деление 10 000 — на 99 % ширины
        edge.Last().Ms.ShouldBe(10_000);
        edge.Last().Label.ShouldBeEmpty();
        VideoTimelineModel.Ticks(0).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Процент положения зажат в 0..100; для CSS — с точкой, не запятой")]
    public void Percent_is_clamped_and_invariant()
    {
        VideoTimelineModel.Percent(5_000, 20_000).ShouldBe(25);
        VideoTimelineModel.Percent(-1, 20_000).ShouldBe(0);
        VideoTimelineModel.Percent(30_000, 20_000).ShouldBe(100);
        VideoTimelineModel.Css(12.3456).ShouldBe("12.346%");
    }

    [Fact(DisplayName = "Часы записи: метаданные файла — точно, до миллисекунды, в поясе страницы")]
    public void Clock_from_metadata_is_exact()
    {
        var recorded = new DateTimeOffset(2022, 12, 8, 8, 8, 12, TimeSpan.Zero);

        var clock = VideoTimelineModel.ClockBase(recorded, new DateTimeOffset(2022, 12, 8, 8, 0, 0, TimeSpan.Zero), Utc6)
            .ShouldNotBeNull();

        clock.Approximate.ShouldBeFalse();
        VideoTimelineModel.RecordTime(clock, 9_345).ShouldBe("08.12.2022 14:08:21.345");
        VideoTimelineModel.RecordClockHint(clock).ShouldContain("не доказательство");
    }

    [Fact(DisplayName = "Часы записи без метаданных — по дате съёмки, «≈» и без миллисекунд; без обеих дат — нет")]
    public void Clock_from_captured_at_is_approximate()
    {
        var captured = new DateTimeOffset(2022, 12, 8, 14, 8, 0, TimeSpan.FromHours(6));

        var clock = VideoTimelineModel.ClockBase(null, captured, Utc6).ShouldNotBeNull();

        clock.Approximate.ShouldBeTrue();
        VideoTimelineModel.RecordTime(clock, 61_500).ShouldBe("≈ 08.12.2022 14:09:01");
        VideoTimelineModel.ClockBase(null, null, Utc6).ShouldBeNull();
    }

    [Fact(DisplayName = "Цвета фигурантов различны в пределах палитры и повторяются по кругу")]
    public void Person_colors_cycle()
    {
        Enumerable.Range(0, 8).Select(VideoTimelineModel.PersonColor).Distinct().Count().ShouldBe(8);
        VideoTimelineModel.PersonColor(8).ShouldBe(VideoTimelineModel.PersonColor(0));
        VideoTimelineModel.Fill("#e53935").ShouldBe("#e5393559"); // заливка — тот же цвет, 35 % непрозрачности
    }
}
