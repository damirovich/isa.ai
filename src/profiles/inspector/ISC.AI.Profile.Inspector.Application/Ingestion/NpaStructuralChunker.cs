using System.Text;
using System.Text.RegularExpressions;
using ISC.AI.Abstractions.Ingestion;

namespace ISC.AI.Profile.Inspector.Application.Ingestion;

/// <summary>
/// Структурный чанкер НПА (инж-ТЗ §5.3.1.3, ТО-мат-03): режет текст по границам «Статья N»/«Глава»/«Раздел»,
/// чтобы единицей извлечения был связный фрагмент нормы (статья/пункт), а не страница. Расширение, НЕ
/// ломающее нейтральный <see cref="ITextChunker"/>: текст без структуры НПА разбивается абзацным
/// упаковщиком (поведение как у ядрового <c>SimpleTextChunker</c>), поэтому глобальная замена безопасна
/// и для не-НПА документов.
/// </summary>
/// <remarks>
/// Чанк держится ≤ <see cref="HardMaxChars"/> (лимит эмбеддера EmbeddingGemma — 2048 токенов; запас на
/// document-префикс Э4-09); слишком длинная статья до-режется абзацным упаковщиком. Самодостаточен —
/// не тянет зависимость на проект ядра <c>ISC.AI.Ingestion</c> (тонкие ссылки профиля).
/// </remarks>
public sealed partial class NpaStructuralChunker : ITextChunker
{
    /// <summary>Целевой размер чанка при абзацной упаковке (символы).</summary>
    private const int TargetChars = 1000;

    /// <summary>Жёсткий потолок чанка (символы) — запас под лимит эмбеддера.</summary>
    private const int HardMaxChars = 1800;

    /// <inheritdoc />
    public IReadOnlyList<string> Chunk(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var segments = SplitByStructure(text);

        // Структуры НПА не нашли (0/1 сегмент) — абзацный фолбэк по всему тексту (как ядровой чанкер).
        if (segments.Count <= 1)
        {
            return PackParagraphs(text);
        }

        var chunks = new List<string>();
        foreach (var segment in segments)
        {
            var trimmed = segment.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (trimmed.Length <= HardMaxChars)
            {
                chunks.Add(trimmed);
            }
            else
            {
                // Длинная статья — до-режем абзацным упаковщиком, сохраняя порядок.
                chunks.AddRange(PackParagraphs(trimmed));
            }
        }

        return chunks;
    }

    /// <summary>Режет текст на сегменты, начинающиеся с заголовка «Статья/Глава/Раздел N».</summary>
    private static List<string> SplitByStructure(string text)
    {
        var headers = StructureHeader().Matches(text);
        if (headers.Count == 0)
        {
            return [text];
        }

        var segments = new List<string>();

        // Преамбула до первого заголовка (название/реквизиты акта) — отдельным сегментом.
        var firstStart = headers[0].Index;
        if (firstStart > 0)
        {
            var preamble = text[..firstStart].Trim();
            if (preamble.Length > 0)
            {
                segments.Add(preamble);
            }
        }

        for (var i = 0; i < headers.Count; i++)
        {
            var start = headers[i].Index;
            var end = i + 1 < headers.Count ? headers[i + 1].Index : text.Length;
            segments.Add(text[start..end]);
        }

        return segments;
    }

    /// <summary>Абзацная упаковка (фолбэк): абзацы в чанки до <see cref="TargetChars"/>; длинный абзац режется по длине.</summary>
    private static List<string> PackParagraphs(string text)
    {
        var chunks = new List<string>();
        var current = new StringBuilder();

        foreach (var rawParagraph in ParagraphSeparator().Split(text))
        {
            var paragraph = rawParagraph.Trim();
            if (paragraph.Length == 0)
            {
                continue;
            }

            if (paragraph.Length > HardMaxChars)
            {
                Flush(chunks, current);
                for (var offset = 0; offset < paragraph.Length; offset += HardMaxChars)
                {
                    chunks.Add(paragraph.Substring(offset, Math.Min(HardMaxChars, paragraph.Length - offset)));
                }

                continue;
            }

            if (current.Length + paragraph.Length + 1 > TargetChars)
            {
                Flush(chunks, current);
            }

            if (current.Length > 0)
            {
                current.Append('\n');
            }

            current.Append(paragraph);
        }

        Flush(chunks, current);
        return chunks;
    }

    private static void Flush(List<string> chunks, StringBuilder buffer)
    {
        if (buffer.Length > 0)
        {
            chunks.Add(buffer.ToString());
            buffer.Clear();
        }
    }

    // Заголовок структурной единицы НПА в начале строки: «Статья 5», «Глава 2», «Раздел 3».
    [GeneratedRegex(@"^[ \t]*(?:Стать[яи]|Глава|Раздел)\b[ \t]+\d+", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StructureHeader();

    // Разделитель абзацев — пустая строка.
    [GeneratedRegex(@"\r?\n[ \t]*\r?\n", RegexOptions.CultureInvariant)]
    private static partial Regex ParagraphSeparator();
}
