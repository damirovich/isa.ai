using System;
using System.Linq;
using Shouldly;
using Xunit;

namespace ISC.AI.Speech.Worker.Tests;

/// <summary>
/// Кольцевая история сырых отсчётов (<see cref="SampleHistory"/>) — источник добавки перед участком речи
/// (pre-roll, ADR-0026): номера отсчётов от начала записи, вытеснение старых, чтение через границу кольца.
/// </summary>
public sealed class SampleHistoryTests
{
    [Fact(DisplayName = "История: пока не заполнена — помнит всё с нуля")]
    public void History_remembers_everything_until_full()
    {
        var history = new SampleHistory(10);
        history.Append(Range(0, 6));

        history.Start.ShouldBe(0);
        history.End.ShouldBe(6);
        Read(history, 2, 3).ShouldBe(Range(2, 3));
    }

    [Fact(DisplayName = "История: старые отсчёты вытесняются, чтение через границу кольца даёт исходный порядок")]
    public void History_wraps_around()
    {
        var history = new SampleHistory(10);
        foreach (var chunk in new[] { Range(0, 7), Range(7, 7), Range(14, 3) })
        {
            history.Append(chunk);
        }

        history.End.ShouldBe(17);
        history.Start.ShouldBe(7);
        Read(history, 7, 10).ShouldBe(Range(7, 10));
        Read(history, 12, 4).ShouldBe(Range(12, 4));
    }

    [Fact(DisplayName = "История: порция длиннее ёмкости — остаются её последние отсчёты, номера не сбиваются")]
    public void Chunk_longer_than_capacity_keeps_its_tail()
    {
        var history = new SampleHistory(8);
        history.Append(Range(0, 3));
        history.Append(Range(3, 20));

        history.End.ShouldBe(23);
        history.Start.ShouldBe(15);
        Read(history, 15, 8).ShouldBe(Range(15, 8));
    }

    [Theory(DisplayName = "История: чтение того, что уже забыто или ещё не пришло, — явная ошибка, а не чужой звук")]
    [InlineData(6L, 3)]
    [InlineData(15L, 3)]
    public void Reading_outside_history_throws(long from, int count)
    {
        var history = new SampleHistory(10);
        history.Append(Range(0, 17));

        Should.Throw<ArgumentOutOfRangeException>(() => history.CopyTo(from, new float[count]));
    }

    private static float[] Range(int start, int count) => Enumerable.Range(start, count).Select(i => (float)i).ToArray();

    private static float[] Read(SampleHistory history, long from, int count)
    {
        var buffer = new float[count];
        history.CopyTo(from, buffer);
        return buffer;
    }
}
