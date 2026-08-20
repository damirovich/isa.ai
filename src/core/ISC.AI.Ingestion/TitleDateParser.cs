using System.Globalization;
using System.Text.RegularExpressions;

namespace ISC.AI.Ingestion;

/// <summary>
/// Дата документа из его заголовка — для материалов, у которых источник отдаёт заголовок, но не дату
/// (пакеты ЦБД Минюста: «Кодекс КР от 28 октября 2021 года № 128 „…“»). Нейтрально к типу документа:
/// распознаётся русская датировка «от 5 мая 2021 года» / «от 05.05.2021», а не что-либо о нормах.
/// </summary>
/// <remarks>
/// Берётся ПЕРВАЯ дата после «от» — у НПА она всегда дата принятия; даты внутри кавычек названия
/// (акт «О внесении изменений в закон от 1999 года…») идут позже и не берутся. Не распознано —
/// <see langword="null"/>, не исключение: дата в каталоге — удобство, не инвариант.
/// </remarks>
public static partial class TitleDateParser
{
    private static readonly Dictionary<string, int> Months = new(StringComparer.OrdinalIgnoreCase)
    {
        ["января"] = 1, ["февраля"] = 2, ["марта"] = 3, ["апреля"] = 4, ["мая"] = 5, ["июня"] = 6,
        ["июля"] = 7, ["августа"] = 8, ["сентября"] = 9, ["октября"] = 10, ["ноября"] = 11, ["декабря"] = 12,
    };

    /// <summary>Дата из заголовка или <see langword="null"/>.</summary>
    public static DateOnly? TryParse(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var words = WordsPattern().Match(title);
        if (words.Success
            && int.TryParse(words.Groups["d"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var day)
            && Months.TryGetValue(words.Groups["m"].Value, out var month)
            && int.TryParse(words.Groups["y"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var year)
            && IsValid(year, month, day))
        {
            return new DateOnly(year, month, day);
        }

        var digits = DigitsPattern().Match(title);
        if (digits.Success
            && int.TryParse(digits.Groups["d"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out day)
            && int.TryParse(digits.Groups["m"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out month)
            && int.TryParse(digits.Groups["y"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out year)
            && IsValid(year, month, day))
        {
            return new DateOnly(year, month, day);
        }

        return null;
    }

    private static bool IsValid(int year, int month, int day) =>
        year is >= 1900 and <= 2100 && month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year, month);

    // «от 28 октября 2021 года», «от 5 мая 2021 г.», «от 28 октября 2021»
    [GeneratedRegex(@"\bот\s+(?<d>\d{1,2})\s+(?<m>[А-Яа-яЁё]+)\s+(?<y>\d{4})", RegexOptions.CultureInvariant)]
    private static partial Regex WordsPattern();

    // «от 28.10.2021»
    [GeneratedRegex(@"\bот\s+(?<d>\d{1,2})\.(?<m>\d{1,2})\.(?<y>\d{4})", RegexOptions.CultureInvariant)]
    private static partial Regex DigitsPattern();
}
