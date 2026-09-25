namespace ISC.AI.Speech.Worker;

/// <summary>
/// Кольцевая история последних сырых отсчётов записи — нужна, чтобы добавить к участку речи звук перед его
/// началом (<see cref="VadSettings.PreRollSamples"/>): детектор отдаёт участок с опозданием, а отсчёты до
/// начала участка в его собственном буфере уже не хранятся. Номера отсчётов — от начала записи, как у детектора.
/// Ёмкость и оценка памяти — <see cref="VadSettings.HistorySeconds"/>.
/// </summary>
internal sealed class SampleHistory
{
    private readonly float[] _ring;

    /// <summary>Создаёт историю на <paramref name="capacity"/> последних отсчётов.</summary>
    public SampleHistory(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _ring = new float[capacity];
    }

    /// <summary>Номер следующего отсчёта — сколько отсчётов добавлено за всё время.</summary>
    public long End { get; private set; }

    /// <summary>Самый старый отсчёт, который история ещё помнит.</summary>
    public long Start => Math.Max(0, End - _ring.Length);

    /// <summary>Добавляет очередные отсчёты записи; самые старые вытесняются.</summary>
    public void Append(ReadOnlySpan<float> samples)
    {
        // Длиннее ёмкости — достаточно последних Capacity отсчётов.
        var skipped = Math.Max(0, samples.Length - _ring.Length);
        var tail = samples[skipped..];
        End += skipped;

        var position = (int)(End % _ring.Length);
        var first = Math.Min(tail.Length, _ring.Length - position);
        tail[..first].CopyTo(_ring.AsSpan(position));
        tail[first..].CopyTo(_ring);
        End += tail.Length;
    }

    /// <summary>Копирует отсчёты [<paramref name="from"/>, <paramref name="from"/> + длина приёмника).</summary>
    /// <exception cref="ArgumentOutOfRangeException">Диапазон выходит за пределы того, что история помнит.</exception>
    public void CopyTo(long from, Span<float> destination)
    {
        if (from < Start || from + destination.Length > End)
        {
            throw new ArgumentOutOfRangeException(nameof(from),
                $"Отсчётов [{from}, {from + destination.Length}) нет в истории [{Start}, {End}).");
        }

        var position = (int)(from % _ring.Length);
        var first = Math.Min(destination.Length, _ring.Length - position);
        _ring.AsSpan(position, first).CopyTo(destination);
        _ring.AsSpan(0, destination.Length - first).CopyTo(destination[first..]);
    }
}
