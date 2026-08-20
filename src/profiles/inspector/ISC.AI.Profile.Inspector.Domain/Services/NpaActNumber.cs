using System.Text.RegularExpressions;

namespace ISC.AI.Profile.Inspector.Domain.Services;

/// <summary>
/// Номер акта из заголовка НПА — для колонки «№» каталога: отдельным полем корпус номер не хранит,
/// в заголовке ЦБД он есть всегда («…от 28 июля 2026 года № 135 "О внесении…"», «№ 62-XII»).
/// Тот же приём, что дата из заголовка при импорте (TitleDateParser ядра).
/// </summary>
public static partial class NpaActNumber
{
    /// <summary>Первый «№ …» заголовка (номер самого акта; упоминания в кавычках идут позже) или <see langword="null"/>.</summary>
    public static string? TryParse(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var match = NumberPattern().Match(title);
        return match.Success ? match.Groups["n"].Value : null;
    }

    // «№ 135», «№62-XII», «№ 1215-XII»: цифры с необязательным буквенным/римским хвостом через дефис.
    [GeneratedRegex(@"№\s*(?<n>\d+(?:[-–][0-9IVXLC]+|[-–][А-Яа-я]+)?)", RegexOptions.CultureInvariant)]
    private static partial Regex NumberPattern();
}
