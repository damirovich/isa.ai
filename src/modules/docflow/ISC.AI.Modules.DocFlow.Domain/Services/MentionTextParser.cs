using System.Globalization;
using System.Text.RegularExpressions;

namespace ISC.AI.Modules.DocFlow.Domain.Services;

/// <summary>
/// Разбор упоминаний в тексте комментария (ТЗ СКИД §4.8). Формат токена — перенос из СКИД, но с
/// идентификатором ПОЛЬЗОВАТЕЛЯ в конвенции ISC.AI (<c>int</c> вместо <c>Guid</c> СКИД):
/// <c>@[Фамилия И.О.](user:42)</c>. Чистая функция — тестируется без БД.
/// </summary>
public static partial class MentionTextParser
{
    [GeneratedRegex(@"@\[[^\]]*\]\(user:([^)]+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex MentionPattern { get; }

    /// <summary>
    /// Извлекает идентификаторы упомянутых из текста. Нечисловые идентификаторы молча игнорируются
    /// (текст пишет пользователь — «мусорный» токен не должен ронять сохранение), повторы схлопываются.
    /// </summary>
    public static IReadOnlyList<int> ExtractMentionedUserIds(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return [];
        }

        var ids = new List<int>();
        var seen = new HashSet<int>();
        foreach (var match in MentionPattern.Matches(content).Cast<Match>())
        {
            var raw = match.Groups[1].Value;
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && seen.Add(id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }
}
