using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ISC.AI.Profile.Investigation.Domain.Services;

namespace ISC.AI.Profile.Investigation.Application.Features.Briefs;

/// <summary>
/// Тексты справки (ADR-0053): факты строками для модели, очистка изложения от разметки, проверка кодом, что числа и
/// даты изложения есть в фактах.
/// </summary>
public static partial class BriefTexts
{
    /// <summary>Строк одной таблицы в тексте для модели (остальное — числом).</summary>
    public const int ModelRowsPerTable = 40;

    /// <summary>Наибольшая длина текста фактов для модели.</summary>
    public const int ModelTextLength = 24_000;

    /// <summary>Наибольшая длина изложения, принимаемого к выгрузке.</summary>
    public const int MaxSummaryLength = 20_000;

    /// <summary>
    /// Все факты справки строками, без сокращений, — основа проверки изложения (<see cref="NotInFacts(string, Brief)"/>).
    /// </summary>
    public static string Full(Brief brief) => Render(brief, int.MaxValue, forModel: false);

    /// <summary>
    /// Факты для задачного промпта: таблицы — не длиннее <see cref="ModelRowsPerTable"/> строк, со строкой «ещё N — в
    /// таблице справки»; весь текст — не длиннее <see cref="ModelTextLength"/> с той же пометкой (без молчаливого
    /// обрезания). Шапка — вид справки и период, без номера дела и имени. Знак «№» заменён словом «номер»: модель
    /// повторяет его, а проверка ссылок ядра считает «№ N» ссылкой на документ, которой нет во фрагментах (ADR-0053).
    /// Скобки меток «⟦…⟧» из значений убраны: шаблон огораживает ими данные, и текст записи (примечание, обоснование) не
    /// может «закрыть» данные и продолжиться как указание модели.
    /// </summary>
    public static string ForModel(Brief brief)
    {
        var text = Render(brief, ModelRowsPerTable, forModel: true);
        return text.Length <= ModelTextLength
            ? text
            : Cut(text, ModelTextLength) + "\n… данные для модели укорочены; полностью — в таблицах справки.";
    }

    /// <summary>
    /// Изложение без разметки модели — для экрана после правки и для файла: заголовки «#», жирный «**», пункты «* »/«- »
    /// становятся обычным текстом и «– », пустые строки схлопываются. HTML не интерпретируется нигде.
    /// </summary>
    public static string Plain(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return string.Empty;
        }

        var lines = new List<string>();
        foreach (var raw in summary.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            var line = raw.Trim().Replace("**", string.Empty, StringComparison.Ordinal).Replace("__", string.Empty, StringComparison.Ordinal);
            line = line.TrimStart('#').Trim();
            if (line.StartsWith("* ", StringComparison.Ordinal) || line.StartsWith("- ", StringComparison.Ordinal)
                || line.StartsWith("• ", StringComparison.Ordinal) || line.StartsWith("+ ", StringComparison.Ordinal))
            {
                line = "– " + line[2..].TrimStart();
            }

            if (line.Length == 0 && (lines.Count == 0 || lines[^1].Length == 0))
            {
                continue;
            }

            lines.Add(line);
        }

        while (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        var text = string.Join("\n", lines);
        return text.Length <= MaxSummaryLength ? text : Cut(text, MaxSummaryLength);
    }

    /// <summary>
    /// Первые <paramref name="length"/> символов без разрыва суррогатной пары (эмодзи и др.): половина пары — символ,
    /// недопустимый в XML, и выгрузка .docx упала бы на каждом открытии такой справки.
    /// </summary>
    public static string Cut(string value, int length)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length <= length)
        {
            return value;
        }

        return length > 0 && char.IsHighSurrogate(value[length - 1]) ? value[..(length - 1)] : value[..length];
    }

    /// <summary>
    /// Числа, даты и время из изложения, которых нет в фактах справки (ADR-0053): модель могла досчитать или исказить.
    /// Известные числа — из всех фактов (<see cref="Full"/>) и из того, что видела модель (<see cref="ForModel"/>: «ещё N
    /// записей»), без служебной нумерации разделов: иначе числа 1…9 считались бы фактами всегда, и ошибки счёта в
    /// самых частых малых числах не ловились бы.
    /// </summary>
    /// <param name="summary">Изложение.</param>
    /// <param name="brief">Справка, по фактам которой написано изложение.</param>
    public static IReadOnlyList<string> NotInFacts(string? summary, Brief brief) =>
        NotInFacts(summary, Full(brief) + "\n" + ForModel(brief));

    /// <summary>
    /// Числа, даты и время из изложения, которых нет в тексте фактов. Порядок первого упоминания, без повторов.
    /// Сравнение — по записи в тексте фактов (запятая и точка в дробях равны); номера разделов «Раздел N.» фактами не
    /// считаются; дата словами («3 сентября 2026») сверяется как «03.09.2026», время «14:30» — с «14:30:05».
    /// </summary>
    /// <param name="summary">Изложение.</param>
    /// <param name="facts">Факты справки строками.</param>
    public static IReadOnlyList<string> NotInFacts(string? summary, string facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (string.IsNullOrWhiteSpace(summary))
        {
            return [];
        }

        var known = new HashSet<string>(
            NumberPattern().Matches(SectionHeaderPattern().Replace(facts, string.Empty)).Select(m => Canonical(m.Value)),
            StringComparer.Ordinal);
        var result = new List<string>();
        foreach (Match match in NumberPattern().Matches(WordDatePattern().Replace(summary, NumericDate)))
        {
            var value = match.Value;
            if (!IsKnown(Canonical(value), known) && !result.Contains(value))
            {
                result.Add(value);
            }
        }

        return result;
    }

    // Изложение вправе сократить дату и время: «01.09» и «2026» есть в фактах, если там есть «01.09.2026»; «14:30» —
    // если там есть «14:30:05».
    private static bool IsKnown(string token, HashSet<string> known)
    {
        if (known.Contains(token))
        {
            return true;
        }

        if (DayMonthPattern().IsMatch(token))
        {
            return known.Any(k => k.StartsWith(token + ".", StringComparison.Ordinal));
        }

        if (TimePattern().IsMatch(token))
        {
            return known.Any(k => k.StartsWith(token + ":", StringComparison.Ordinal));
        }

        return YearPattern().IsMatch(token) && known.Any(k => k.EndsWith("." + token, StringComparison.Ordinal));
    }

    private static readonly string[] Months =
        ["января", "февраля", "марта", "апреля", "мая", "июня", "июля", "августа", "сентября", "октября", "ноября", "декабря"];

    // «3 сентября 2026» → «03.09.2026», «3 сентября» → «03.09».
    private static string NumericDate(Match match)
    {
        var day = int.Parse(match.Groups["day"].Value, CultureInfo.InvariantCulture);
        var month = Array.IndexOf(Months, match.Groups["month"].Value.ToLowerInvariant()) + 1;
        var text = day.ToString("00", CultureInfo.InvariantCulture) + "." + month.ToString("00", CultureInfo.InvariantCulture);
        return match.Groups["year"].Success ? text + "." + match.Groups["year"].Value : text;
    }

    // Текст фактов: «Раздел N. Заголовок», строки «поле: значение», таблица строками «a | b | c».
    private static string Render(Brief brief, int rowsPerTable, bool forModel)
    {
        ArgumentNullException.ThrowIfNull(brief);
        var sb = new StringBuilder();
        if (forModel)
        {
            // Шапка для модели — без номера дела (ADR-0053 п. 5): номер есть в разделах, а из первой строки модель
            // повторяет его охотнее всего. Имя фигуранта и название дела — тоже в разделах.
            sb.AppendLine(brief.Kind == BriefKind.Person ? "Справка по фигуранту" : "Справка по делу");
            if (brief.From is { } from && brief.To is { } to)
            {
                sb.Append("Период: ").Append(from.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)).Append(" — ")
                    .AppendLine(to.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture));
            }
        }
        else
        {
            sb.AppendLine(Clean(brief.Title, forModel));
            sb.AppendLine(Clean(brief.Subtitle, forModel));
        }
        var number = 0;
        foreach (var section in brief.Sections)
        {
            number++;
            sb.AppendLine();
            sb.Append("Раздел ").Append(number.ToString(CultureInfo.InvariantCulture)).Append(". ").AppendLine(Clean(section.Title, forModel));
            if (section.IsEmpty)
            {
                sb.AppendLine("Сведений нет.");
                continue;
            }

            foreach (var fact in section.Facts)
            {
                sb.Append(Clean(fact.Label, forModel)).Append(": ").AppendLine(Clean(fact.Value, forModel));
            }

            if (section.Table is { TotalRows: > 0 } table)
            {
                sb.Append("Таблица (записей: ").Append(table.TotalRows.ToString(CultureInfo.InvariantCulture)).Append("): ")
                    .AppendLine(string.Join(" | ", table.Columns.Select(c => Clean(c, forModel))));
                foreach (var row in table.Rows.Take(rowsPerTable))
                {
                    sb.AppendLine(string.Join(" | ", row.Select(c => Clean(c, forModel))));
                }

                var omitted = table.TotalRows - Math.Min(table.Rows.Count, rowsPerTable);
                if (omitted > 0)
                {
                    sb.Append("… ещё ").Append(omitted.ToString(CultureInfo.InvariantCulture)).AppendLine(" записей — в таблице справки.");
                }
            }

            if (!string.IsNullOrWhiteSpace(section.Note))
            {
                sb.Append("Примечание: ").AppendLine(Clean(section.Note, forModel));
            }
        }

        // Перевод строки — один символ LF на любой ОС: промпт и проверка изложения не зависят от сервера.
        return sb.ToString().Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();
    }

    // Одна строка; для модели — без «№» и без скобок меток данных (см. ForModel).
    private static string Clean(string? value, bool forModel)
    {
        var text = string.Join(' ', (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return forModel
            ? text.Replace("№", "номер", StringComparison.Ordinal)
                .Replace("⟦", string.Empty, StringComparison.Ordinal)
                .Replace("⟧", string.Empty, StringComparison.Ordinal)
            : text;
    }

    private static string Canonical(string value) => value.Replace(',', '.');

    // Число с разделителями внутри: «3», «0,87», «01.09.2026», «14:30», «12-2026/045». Разделитель в конце не берётся.
    [GeneratedRegex(@"\d+(?:[.,:/\-]\d+)*", RegexOptions.CultureInvariant)]
    private static partial Regex NumberPattern();

    [GeneratedRegex(@"^\d{1,2}\.\d{1,2}$", RegexOptions.CultureInvariant)]
    private static partial Regex DayMonthPattern();

    [GeneratedRegex(@"^(19|20)\d{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex YearPattern();

    [GeneratedRegex(@"^\d{1,2}:\d{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex TimePattern();

    // Служебная нумерация текста фактов: «Раздел 3. » в начале строки.
    [GeneratedRegex(@"^Раздел \d+\. ", RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex SectionHeaderPattern();

    [GeneratedRegex(
        @"(?<!\d)(?<day>\d{1,2})\s+(?<month>января|февраля|марта|апреля|мая|июня|июля|августа|сентября|октября|ноября|декабря)(?:\s+(?<year>(?:19|20)\d{2})(?!\d))?",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex WordDatePattern();
}
