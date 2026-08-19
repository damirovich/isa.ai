using System.Text;
using System.Text.RegularExpressions;
using ISC.AI.Abstractions.Ingestion;

namespace ISC.AI.Profile.Inspector.Application.Features.Ingestion;

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
                chunks.AddRange(SplitLongParagraph(paragraph));
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

    /// <summary>
    /// Длинный абзац (без пустых строк внутри) режется по ГРАНИЦАМ, не по длине: сначала по концу
    /// предложения, если его нет в окне — по пробелу, и только для слова длиннее окна — по длине.
    /// Раньше резалось Substring'ом по HardMaxChars — посреди слова («…бюджет Кыргызско» / «й Республики…»),
    /// и такой обрывок был мусором для поиска.
    /// </summary>
    private static List<string> SplitLongParagraph(string paragraph)
    {
        var pieces = new List<string>();
        var start = 0;
        while (start < paragraph.Length)
        {
            var remaining = paragraph.Length - start;
            if (remaining <= HardMaxChars)
            {
                pieces.Add(paragraph[start..].Trim());
                break;
            }

            var windowEnd = start + HardMaxChars;
            var cut = LastBoundary(paragraph, start, windowEnd, SentenceEnd)
                ?? LastBoundary(paragraph, start, windowEnd, char.IsWhiteSpace)
                ?? windowEnd;
            var piece = paragraph[start..cut].Trim();
            if (piece.Length > 0)
            {
                pieces.Add(piece);
            }

            start = cut;
        }

        return pieces;
    }

    // Последняя позиция в окне [from, to), ПОСЛЕ которой можно резать; не ближе половины окна к началу —
    // иначе один ранний знак препинания давал бы крошечный кусок и гигантский хвост.
    private static int? LastBoundary(string text, int from, int to, Func<char, bool> isBoundary)
    {
        var minimum = from + HardMaxChars / 2;
        for (var i = to - 1; i > minimum; i--)
        {
            if (isBoundary(text[i]))
            {
                return i + 1;
            }
        }

        return null;
    }

    private static bool SentenceEnd(char ch) => ch is '.' or ';' or '!' or '?';

    private static void Flush(List<string> chunks, StringBuilder buffer)
    {
        if (buffer.Length > 0)
        {
            chunks.Add(buffer.ToString());
            buffer.Clear();
        }
    }

    // Заголовок структурной единицы НПА: «Статья 5.», «Глава II», «Раздел 3» — в начале строки ИЛИ
    // внутри строки (пакеты, собранные до сохранения структуры, шли одной строкой). Внутри строки
    // требуется заглавная буква и точка/перенос после номера — ссылка «в статье 3 настоящего Закона»
    // (строчная, без точки) заголовком не считается. Номер главы/раздела может быть римским.
    [GeneratedRegex(@"(?:^[ \t]*|(?<=[\s.;:)]))(?:Статья|Глава|Раздел|СТАТЬЯ|ГЛАВА|РАЗДЕЛ)\s+(?:\d+|[IVXLC]+)(?:[-–]\d+)?(?=\s*[.\n]|\s*$)", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex StructureHeader();

    // Разделитель абзацев — пустая строка.
    [GeneratedRegex(@"\r?\n[ \t]*\r?\n", RegexOptions.CultureInvariant)]
    private static partial Regex ParagraphSeparator();
}
