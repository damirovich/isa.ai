using System;
using System.Linq;
using ISC.AI.Modules.Media.Application.Features.Indexing;
using ISC.AI.Modules.Media.Domain.Services;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Media;

/// <summary>
/// Отбор кадров раскадровки в ленту видео (ADR-0038): без знания длительности, прореживанием — кадры ленты всегда идут
/// с равным шагом от начала записи, их не больше предела и не меньше половины предела (у коротких записей — все).
/// </summary>
public sealed class FilmstripCollectorTests
{
    /// <summary>Прогон «раскадровки» 1 к/с длиной <paramref name="frames"/> кадров через сборщик.</summary>
    private static FilmstripCollector Run(int frames, int maxTiles, Func<int, bool>? fails = null)
    {
        var collector = new FilmstripCollector(maxTiles);
        for (var i = 0; i < frames; i++)
        {
            if (collector.Offer() && !(fails?.Invoke(i) ?? false))
            {
                collector.Add(i * 1000L, [(byte)(i % 256)]);
            }
        }

        return collector;
    }

    [Fact(DisplayName = "Короткая запись: берутся все кадры, шаг — шаг раскадровки")]
    public void Short_video_keeps_every_frame()
    {
        var collector = Run(frames: 7, maxTiles: 10);

        collector.Tiles.Select(t => t.TimestampMs).ShouldBe([0, 1000, 2000, 3000, 4000, 5000, 6000]);
        collector.StepMs.ShouldBe(1000);
    }

    [Theory(DisplayName = "Длинная запись: кадров от половины предела до предела, шаг равный и кратен шагу раскадровки")]
    [InlineData(11, 10)]
    [InlineData(100, 10)]
    [InlineData(3600, 120)]
    [InlineData(5000, 120)]
    public void Long_video_is_thinned_evenly(int frames, int maxTiles)
    {
        var collector = Run(frames, maxTiles);
        var times = collector.Tiles.Select(t => t.TimestampMs).ToList();

        times.Count.ShouldBeInRange(maxTiles / 2, maxTiles);
        times[0].ShouldBe(0);
        var step = times[1] - times[0];
        (step % 1000).ShouldBe(0);
        for (var i = 1; i < times.Count; i++)
        {
            (times[i] - times[i - 1]).ShouldBe(step, $"кадр {i}");
        }

        collector.StepMs.ShouldBe(step);
    }

    [Fact(DisplayName = "Пропущенный кадр (не уменьшился) не сбивает шаг: прореживание — по номеру кадра выборки")]
    public void Skipped_frame_keeps_grid()
    {
        var collector = Run(frames: 40, maxTiles: 10, fails: i => i == 2);
        var times = collector.Tiles.Select(t => t.TimestampMs).ToList();

        times.ShouldAllBe(t => t % 4000 == 0);
        times.Count.ShouldBeInRange(5, 10);
    }

    [Fact(DisplayName = "Один кадр — шаг 0; предел меньше двух — ошибка")]
    public void Single_tile_and_bad_limit()
    {
        Run(frames: 1, maxTiles: 10).StepMs.ShouldBe(0);
        Should.Throw<ArgumentOutOfRangeException>(() => new FilmstripCollector(1));
    }

    [Theory(DisplayName = "Ширина плитки — по пропорциям кадра при высоте 72; крайние пропорции зажаты; без размера — 16:9")]
    [InlineData(1920, 1080, 128)]
    [InlineData(640, 480, 96)]
    [InlineData(1080, 1920, 40)]
    [InlineData(4000, 100, 216)]
    [InlineData(100, 4000, 36)]
    [InlineData(0, 0, 128)]
    public void Tile_width_follows_aspect(int width, int height, int expected)
    {
        MediaIndexer.FilmstripTileWidth(new ImageSize(width, height)).ShouldBe(expected);
    }
}
