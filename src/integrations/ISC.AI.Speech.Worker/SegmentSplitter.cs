namespace ISC.AI.Speech.Worker;

/// <summary>Кусок участка речи: смещение и длина в отсчётах относительно начала участка.</summary>
/// <param name="Offset">Смещение от начала участка, отсчёты.</param>
/// <param name="Length">Длина, отсчёты.</param>
internal readonly record struct SegmentPiece(int Offset, int Length);

/// <summary>
/// Нарезка слишком длинного участка речи на куски, которые модель может принять (ADR-0026: GigaAM берёт
/// до ~25 с за раз). Предел куска держит ТОЛЬКО эта нарезка: собственный принудительный разрез детектора речи
/// (<c>MaxSpeechDuration</c>) теряет звук на стыке участков, поэтому детектору задан заведомо большой предел
/// (<see cref="VadSettings.MaxSpeechSeconds"/>), и длинная речь без пауз приходит сюда одним участком.
/// Также режет запись целиком в диагностическом режиме <c>--decode-whole</c>.
/// </summary>
/// <remarks>
/// ПОЧЕМУ РЕЖЕМ ПО САМОМУ ТИХОМУ МЕСТУ, А НЕ РОВНО ПО ГРАНИЦЕ. Разрез посреди слова портит это слово в
/// ОБОИХ кусках, а для следствия дословность важнее ровных кусков. Поэтому в последних секундах перед
/// пределом ищется окно с наименьшей энергией (вдох, пауза между словами) и разрез делается в его
/// середине. Куски идут встык, без перекрытия: перекрытие дало бы повтор слов на стыке, который в
/// дословной расшифровке выглядел бы как сказанное дважды.
/// </remarks>
internal static class SegmentSplitter
{
    /// <summary>
    /// Делит участок длиной <paramref name="samples"/>.Length на куски не длиннее <paramref name="maxSamples"/>.
    /// Куски покрывают участок целиком, встык и по порядку.
    /// </summary>
    /// <param name="samples">Отсчёты участка.</param>
    /// <param name="maxSamples">Наибольшая длина куска, отсчёты.</param>
    /// <param name="searchSamples">Сколько отсчётов перед пределом просматривать в поисках тихого места.</param>
    /// <param name="frameSamples">Окно оценки энергии, отсчёты (при 16 кГц 10 мс = 160).</param>
    public static IReadOnlyList<SegmentPiece> Split(ReadOnlySpan<float> samples, int maxSamples, int searchSamples, int frameSamples)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSamples, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(frameSamples, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(searchSamples);

        var pieces = new List<SegmentPiece>();
        var offset = 0;
        while (samples.Length - offset > maxSamples)
        {
            var cut = FindQuietCut(samples, offset, maxSamples, searchSamples, frameSamples);
            pieces.Add(new SegmentPiece(offset, cut - offset));
            offset = cut;
        }

        if (samples.Length - offset > 0)
        {
            pieces.Add(new SegmentPiece(offset, samples.Length - offset));
        }

        return pieces;
    }

    // Точка разреза в (offset, offset + maxSamples]: середина самого тихого окна среди последних
    // searchSamples отсчётов перед пределом; если окно не помещается — ровно по пределу.
    private static int FindQuietCut(ReadOnlySpan<float> samples, int offset, int maxSamples, int searchSamples, int frameSamples)
    {
        var limit = offset + maxSamples;
        var searchStart = Math.Max(offset + 1, limit - searchSamples);
        if (limit - searchStart < frameSamples)
        {
            return limit;
        }

        var bestStart = -1;
        var bestEnergy = double.MaxValue;
        for (var start = searchStart; start + frameSamples <= limit; start += frameSamples)
        {
            double energy = 0;
            foreach (var sample in samples.Slice(start, frameSamples))
            {
                energy += sample * sample;
            }

            // «<=»: из равных по тишине окон берётся ПОЗДНЕЕ — кусок выходит длиннее, кусков меньше.
            if (energy <= bestEnergy)
            {
                bestEnergy = energy;
                bestStart = start;
            }
        }

        return bestStart < 0 ? limit : bestStart + frameSamples / 2;
    }
}
