using System.Globalization;
using System.Text;

namespace ISC.AI.Documents.Extraction;

/// <summary>
/// Простой разбор RTF в текст (ADR-0042) — для <c>.doc</c>, который на деле RTF: так сохраняли документы многие
/// учётные программы. Берётся только текст: абзацы, табуляции, ячейки; служебные части (таблицы шрифтов и цветов,
/// стили, сведения о документе, рисунки, коды полей) пропускаются.
/// </summary>
/// <remarks>
/// Кодировка 8-битных символов <c>\'hh</c> — по шрифту (<c>\fcharset</c>: 204 — кириллица, 1251), иначе по
/// <c>\ansicpg</c> документа, иначе 1252. Символы <c>\uN</c> — Юникод; за ними пропускается <c>\ucN</c> заменителей.
/// Вложенность групп ограничена — защита от «бомбы» из скобок.
/// </remarks>
internal static class RtfText
{
    private const int MaxDepth = 1000;

    // Группы, содержимое которых — не текст документа.
    private static readonly HashSet<string> SkippedDestinations = new(StringComparer.Ordinal)
    {
        "fonttbl", "colortbl", "stylesheet", "info", "pict", "object", "header", "headerl", "headerr", "headerf",
        "footer", "footerl", "footerr", "footerf", "listtable", "listoverridetable", "rsidtbl", "generator",
        "themedata", "colorschememapping", "latentstyles", "datastore", "xmlnstbl", "mmathPr", "pgdsctbl", "fldinst",
        "bkmkstart", "bkmkend", "filetbl", "revtbl", "userprops", "objdata", "blipuid", "shpinst", "nonshppict",
    };

    /// <summary>Файл начинается с <c>{\rtf</c>.</summary>
    public static bool HasSignature(ReadOnlySpan<byte> data) => data.StartsWith("{\\rtf"u8);

    /// <summary>Текст RTF-документа.</summary>
    public static string Extract(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var fonts = ReadFontCodePages(data);
        var output = new StringBuilder();
        var states = new Stack<State>();
        var state = new State(Skip: false, UnicodeSkip: 1, Font: -1);
        var documentCodePage = 1252;
        var pendingBytes = new List<byte>();
        var skipFallback = 0;
        var i = 0;

        void FlushBytes()
        {
            if (pendingBytes.Count == 0)
            {
                return;
            }

            var codePage = state.Font >= 0 && fonts.TryGetValue(state.Font, out var fontPage) ? fontPage : documentCodePage;
            output.Append(Decode([.. pendingBytes], codePage));
            pendingBytes.Clear();
        }

        while (i < data.Length)
        {
            var c = (char)data[i];
            switch (c)
            {
                case '{':
                    FlushBytes();
                    if (states.Count >= MaxDepth)
                    {
                        throw new InvalidDataException("RTF: слишком глубокая вложенность групп.");
                    }

                    states.Push(state);
                    i++;
                    continue;
                case '}':
                    FlushBytes();
                    state = states.Count > 0 ? states.Pop() : state;
                    i++;
                    continue;
                case '\r' or '\n':
                    i++;
                    continue;
                case '\\':
                    break;
                default:
                    FlushBytes();
                    if (skipFallback > 0)
                    {
                        skipFallback--;
                    }
                    else if (!state.Skip)
                    {
                        output.Append(c >= 0x80 ? Decode([data[i]], documentCodePage) : c.ToString());
                    }

                    i++;
                    continue;
            }

            // Управляющее слово или символ после «\».
            i++;
            if (i >= data.Length)
            {
                break;
            }

            var next = (char)data[i];
            if (next == '\'')
            {
                // \'hh — байт 8-битной кодировки; копится, чтобы многобайтные кодировки декодировались целиком.
                if (i + 2 < data.Length && byte.TryParse(
                        Encoding.ASCII.GetString(data, i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
                {
                    if (skipFallback > 0)
                    {
                        skipFallback--;
                    }
                    else if (!state.Skip)
                    {
                        pendingBytes.Add(value);
                    }
                }

                i += 3;
                continue;
            }

            FlushBytes();
            if (!char.IsAsciiLetter(next))
            {
                // Управляющий символ: экранированные скобки и «\», неразрывный пробел, дефисы, «\*» — пропускаемая группа.
                if (!state.Skip)
                {
                    switch (next)
                    {
                        case '\\' or '{' or '}':
                            output.Append(next);
                            break;
                        case '~':
                            output.Append(' ');
                            break;
                        case '_':
                            output.Append('-');
                            break;
                    }
                }

                if (next == '*')
                {
                    state = state with { Skip = true };
                }

                i++;
                continue;
            }

            var wordStart = i;
            while (i < data.Length && char.IsAsciiLetter((char)data[i]))
            {
                i++;
            }

            var word = Encoding.ASCII.GetString(data, wordStart, i - wordStart);
            int? parameter = null;
            var numberStart = i;
            if (i < data.Length && (data[i] == '-' || char.IsAsciiDigit((char)data[i])))
            {
                i++;
                while (i < data.Length && char.IsAsciiDigit((char)data[i]))
                {
                    i++;
                }

                if (int.TryParse(Encoding.ASCII.GetString(data, numberStart, i - numberStart), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number))
                {
                    parameter = number;
                }
            }

            if (i < data.Length && data[i] == ' ')
            {
                i++; // пробел-разделитель — часть управляющего слова
            }

            if (SkippedDestinations.Contains(word))
            {
                state = state with { Skip = true };
                continue;
            }

            switch (word)
            {
                case "ansicpg" when parameter is { } page:
                    documentCodePage = page;
                    break;
                case "f" when parameter is { } font:
                    state = state with { Font = font };
                    break;
                case "uc" when parameter is { } count:
                    state = state with { UnicodeSkip = Math.Max(0, count) };
                    break;
                case "u" when parameter is { } code:
                    if (!state.Skip)
                    {
                        output.Append((char)(code < 0 ? code + 65536 : code));
                    }

                    skipFallback = state.UnicodeSkip;
                    break;
                case "par" or "line" or "row" or "sect" or "page":
                    if (!state.Skip)
                    {
                        output.Append('\n');
                    }

                    break;
                case "tab" or "cell":
                    if (!state.Skip)
                    {
                        output.Append('\t');
                    }

                    break;
                case "emdash":
                    AppendIfText(output, state, '—');
                    break;
                case "endash":
                    AppendIfText(output, state, '–');
                    break;
                case "lquote":
                    AppendIfText(output, state, '‘');
                    break;
                case "rquote":
                    AppendIfText(output, state, '’');
                    break;
                case "ldblquote":
                    AppendIfText(output, state, '“');
                    break;
                case "rdblquote":
                    AppendIfText(output, state, '”');
                    break;
                case "bullet":
                    AppendIfText(output, state, '•');
                    break;
            }
        }

        FlushBytes();
        return output.ToString();
    }

    private static void AppendIfText(StringBuilder output, State state, char value)
    {
        if (!state.Skip)
        {
            output.Append(value);
        }
    }

    // Кодовые страницы шрифтов из \fonttbl: \fN … \fcharsetM. Символы \'hh декодируются по шрифту текущего текста.
    private static Dictionary<int, int> ReadFontCodePages(byte[] data)
    {
        var result = new Dictionary<int, int>();
        var text = Encoding.ASCII.GetString(data, 0, Math.Min(data.Length, 256 * 1024));
        var table = text.IndexOf("\\fonttbl", StringComparison.Ordinal);
        if (table < 0)
        {
            return result;
        }

        var font = -1;
        for (var i = table; i < text.Length - 2; i++)
        {
            if (text[i] != '\\')
            {
                continue;
            }

            if (string.CompareOrdinal(text, i, "\\fcharset", 0, 9) == 0)
            {
                var charset = ReadNumber(text, i + 9);
                if (font >= 0 && charset is { } value && CharsetCodePage(value) is { } page)
                {
                    result[font] = page;
                }
            }
            else if (text[i + 1] == 'f' && char.IsAsciiDigit(text[i + 2]))
            {
                font = ReadNumber(text, i + 2) ?? -1;
            }
            else if (string.CompareOrdinal(text, i, "\\colortbl", 0, 9) == 0 || string.CompareOrdinal(text, i, "\\stylesheet", 0, 11) == 0)
            {
                break;
            }
        }

        return result;
    }

    private static int? ReadNumber(string text, int start)
    {
        var end = start;
        while (end < text.Length && char.IsAsciiDigit(text[end]))
        {
            end++;
        }

        return end > start && int.TryParse(text.AsSpan(start, end - start), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    private static int? CharsetCodePage(int charset) => charset switch
    {
        0 => 1252,
        161 => 1253,
        162 => 1254,
        177 => 1255,
        178 => 1256,
        186 => 1257,
        204 => 1251,
        238 => 1250,
        _ => null,
    };

    private static string Decode(byte[] bytes, int codePage)
    {
        try
        {
            var encoding = CodePagesEncodingProvider.Instance.GetEncoding(codePage) ?? Encoding.Latin1;
            return encoding.GetString(bytes);
        }
        catch (ArgumentException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    private readonly record struct State(bool Skip, int UnicodeSkip, int Font);
}
