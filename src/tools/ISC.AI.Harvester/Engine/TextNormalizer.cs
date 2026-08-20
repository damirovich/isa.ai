using System.Text;
using System.Text.RegularExpressions;

namespace ISC.AI.Harvester.Engine;

/// <summary>Нормализация пробелов извлечённого текста (общий помощник извлечения).</summary>
internal static partial class TextNormalizer
{
    /// <summary>Сжимает любые серии пробельных символов до одного пробела; обрезает края (одна строка).</summary>
    public static string Collapse(string text)
    {
        var builder = new StringBuilder(text.Length);
        var lastWhitespace = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!lastWhitespace)
                {
                    builder.Append(' ');
                }

                lastWhitespace = true;
            }
            else
            {
                builder.Append(ch);
                lastWhitespace = false;
            }
        }

        return builder.ToString().Trim();
    }

    /// <summary>
    /// Нормализация С СОХРАНЕНИЕМ СТРУКТУРЫ: внутри строки пробелы схлопываются, строки обрезаются,
    /// пустые строки сводятся к одной (граница абзаца — ровно одна пустая строка, как ждут чанкеры:
    /// ТО-мат-03), неразрывные пробелы из Word-экспорта — обычными, висячие разделители ячеек убраны.
    /// </summary>
    public static string NormalizeStructured(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var normalized = text.Replace(' ', ' ').Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = new List<string>();
        foreach (var rawLine in normalized.Split('\n'))
        {
            var line = InlineSpaces().Replace(rawLine, " ").Trim();
            // Строка таблицы: убрать хвостовой разделитель и пустые ячейки по краям.
            line = TrailingCellSeparator().Replace(line, string.Empty).Trim();
            if (line.Length == 0)
            {
                if (lines.Count > 0 && lines[^1].Length > 0)
                {
                    lines.Add(string.Empty);
                }

                continue;
            }

            lines.Add(line);
        }

        while (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return string.Join('\n', lines);
    }

    [GeneratedRegex(@"[ \t\f\v]+", RegexOptions.CultureInvariant)]
    private static partial Regex InlineSpaces();

    // « | » в конце строки и пустые ячейки « |  | » подряд.
    [GeneratedRegex(@"(\s*\|\s*)+$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingCellSeparator();
}
