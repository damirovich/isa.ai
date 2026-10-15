using System;
using System.Collections.Generic;
using System.Linq;
using ISC.AI.Modules.Media.Domain.Model;

namespace ISC.AI.Modules.Media.UI;

/// <summary>Вид расшифровки в панели (ADR-0047).</summary>
public enum TranscriptView
{
    /// <summary>Фрагмент — строкой со своим таймкодом.</summary>
    Segments = 0,

    /// <summary>Абзацами для чтения: фрагменты подряд, таймкод — у начала абзаца.</summary>
    Text = 1,
}

/// <summary>Абзац режима «Текстом»: начало и фрагменты по порядку.</summary>
/// <param name="StartMs">Начало абзаца (первого фрагмента), мс.</param>
/// <param name="Segments">Фрагменты абзаца по порядку.</param>
public sealed record TranscriptParagraph(long StartMs, IReadOnlyList<TranscriptSegmentRow> Segments);

/// <summary>
/// Раскладка расшифровки в абзацы для чтения (ADR-0047, режим «Текстом»): фрагменты идут подряд, новый абзац — после
/// паузы не короче <see cref="PauseMs"/> либо когда абзац длиннее <see cref="MaxParagraphMs"/>. Меняется только
/// раскладка: текст фрагментов — дословный первичный слой (ТЭ-002), ни один фрагмент не теряется и не склеивается в
/// одну строку с другим.
/// </summary>
public static class TranscriptParagraphs
{
    /// <summary>Пауза, после которой начинается новый абзац, мс.</summary>
    public const long PauseMs = 2_000;

    /// <summary>Наибольшая длина абзаца по времени, мс: дальше — новый абзац, даже без паузы.</summary>
    public const long MaxParagraphMs = 60_000;

    /// <summary>Разложить фрагменты (порядок — по началу, затем по номеру) в абзацы.</summary>
    /// <param name="segments">Фрагменты расшифровки.</param>
    public static IReadOnlyList<TranscriptParagraph> Group(IReadOnlyList<TranscriptSegmentRow> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var paragraphs = new List<TranscriptParagraph>();
        List<TranscriptSegmentRow>? current = null;
        long start = 0;
        long previousEnd = 0;
        foreach (var segment in segments.OrderBy(s => s.StartMs).ThenBy(s => s.Index))
        {
            if (current is null
                || segment.StartMs - previousEnd >= PauseMs
                || segment.StartMs - start >= MaxParagraphMs)
            {
                current = [];
                start = segment.StartMs;
                paragraphs.Add(new TranscriptParagraph(start, current));
            }

            current.Add(segment);
            previousEnd = Math.Max(previousEnd, segment.EndMs);
        }

        return paragraphs;
    }
}
