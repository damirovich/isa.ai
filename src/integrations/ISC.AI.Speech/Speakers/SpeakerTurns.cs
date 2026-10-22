using ISC.AI.Modules.Media.Domain.Model;

namespace ISC.AI.Speech.Speakers;

/// <summary>Реплика говорящего: участок записи и номер диктора источника (модели или канала), с нуля.</summary>
/// <param name="StartMs">Начало, мс от начала записи.</param>
/// <param name="EndMs">Конец, мс.</param>
/// <param name="Speaker">Номер диктора источника.</param>
public sealed record SpeakerTurnSpan(long StartMs, long EndMs, int Speaker);

/// <summary>
/// Приписывание дикторов фрагментам расшифровки (ADR-0062): каждому фрагменту — диктор реплик с наибольшим суммарным
/// перекрытием по времени; без перекрытия — без диктора. Номера — с 1 по первому появлению в записи, а не номера
/// источника: «Диктор 1» — тот, кто заговорил первым.
/// </summary>
public static class SpeakerAssignment
{
    /// <summary>Фрагменты с дикторами.</summary>
    public static IReadOnlyList<TranscriptSegmentDraft> Assign(IReadOnlyList<TranscriptSegmentDraft> segments, IReadOnlyList<SpeakerTurnSpan> turns)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(turns);
        if (turns.Count == 0)
        {
            return segments;
        }

        var ordered = turns.Where(t => t.EndMs > t.StartMs).OrderBy(t => t.StartMs).ToList();
        var numbers = new Dictionary<int, int>();
        var result = new List<TranscriptSegmentDraft>(segments.Count);
        foreach (var segment in segments.OrderBy(s => s.StartMs).ThenBy(s => s.Index))
        {
            var overlap = new Dictionary<int, long>();
            foreach (var turn in ordered)
            {
                if (turn.StartMs >= segment.EndMs)
                {
                    break;
                }

                var common = Math.Min(turn.EndMs, segment.EndMs) - Math.Max(turn.StartMs, segment.StartMs);
                if (common > 0)
                {
                    overlap[turn.Speaker] = overlap.GetValueOrDefault(turn.Speaker) + common;
                }
            }

            int? speaker = null;
            if (overlap.Count > 0)
            {
                // Наибольшее перекрытие; при равенстве — меньший номер источника (детерминированно).
                var best = overlap.OrderByDescending(o => o.Value).ThenBy(o => o.Key).First().Key;
                if (!numbers.TryGetValue(best, out var number))
                {
                    number = numbers.Count + 1;
                    numbers[best] = number;
                }

                speaker = number;
            }

            result.Add(segment with { Speaker = speaker });
        }

        return result.OrderBy(s => s.Index).ToList();
    }
}
