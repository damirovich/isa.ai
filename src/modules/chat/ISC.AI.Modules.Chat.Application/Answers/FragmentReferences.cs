using System.Globalization;
using System.Text.RegularExpressions;

namespace ISC.AI.Modules.Chat.Application.Answers;

/// <summary>
/// Ссылки ответа на фрагменты («[Фрагмент N]», «во фрагменте N»): номер должен быть среди поданных модели (ADR-0051/0052).
/// Номер вне поданных — выдуманный источник; ответ показывается с предупреждением, как и при непроверенной ссылке.
/// Разметку «[Фрагмент N]» задаёт ядро (<c>GroundedGenerator</c> нумерует фрагменты с 1), поэтому правило одно для
/// всех профилей.
/// </summary>
public static partial class FragmentReferences
{
    /// <summary>Номера фрагментов, на которые ссылается ответ, по порядку первого упоминания, без повторов.</summary>
    public static IReadOnlyList<int> Find(string? answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
        {
            return [];
        }

        var result = new List<int>();
        foreach (Match match in Pattern().Matches(answer))
        {
            if (int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
                && !result.Contains(number))
            {
                result.Add(number);
            }
        }

        return result;
    }

    /// <summary>Номера, которых модели не подавали (вне 1…<paramref name="supplied"/>).</summary>
    public static IReadOnlyList<int> Unknown(string? answer, int supplied) =>
        Find(answer).Where(n => n < 1 || n > supplied).ToList();

    /// <summary>
    /// Ответ на куски для показа: обычный текст и ссылки на фрагменты (<c>Fragment</c> — номер; текст куска — ссылка как
    /// написана, вместе с квадратными скобками, если они есть: экран выделяет её, не переписывая).
    /// </summary>
    public static IReadOnlyList<(string Text, int? Fragment)> Split(string? answer)
    {
        if (string.IsNullOrEmpty(answer))
        {
            return [];
        }

        var parts = new List<(string Text, int? Fragment)>();
        var position = 0;
        foreach (Match match in Pattern().Matches(answer))
        {
            if (match.Index > position)
            {
                parts.Add((answer[position..match.Index], null));
            }

            parts.Add((match.Value, int.Parse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture)));
            position = match.Index + match.Length;
        }

        if (position < answer.Length)
        {
            parts.Add((answer[position..], null));
        }

        return parts;
    }

    // «[Фрагмент 2]», «[фрагмент №2]», «во фрагменте 3»: скобки — часть ссылки, если стоят вплотную.
    [GeneratedRegex(@"(?:\[\s*)?фрагмент[а-яё]*\s*(?:№\s*)?(\d{1,4})(?:\s*\])?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
