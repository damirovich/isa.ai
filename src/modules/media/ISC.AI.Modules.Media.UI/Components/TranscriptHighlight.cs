using System;
using System.Collections.Generic;

namespace ISC.AI.Modules.Media.UI;

/// <summary>Кусок текста фрагмента расшифровки для показа: совпал ли он с искомым.</summary>
/// <param name="Text">Кусок текста как есть (Razor экранирует его при выводе).</param>
/// <param name="IsMatch">Кусок совпал с искомым и выделяется.</param>
public sealed record HighlightPart(string Text, bool IsMatch);

/// <summary>
/// Разбиение текста фрагмента расшифровки на куски «совпало / не совпало» для выделения найденного в выдаче
/// поиска по расшифровкам (ADR-0026).
/// </summary>
/// <remarks>
/// БЕЗОПАСНОСТЬ. Текст фрагмента — вывод модели по записи из материалов дела, т.е. по сути пользовательские
/// данные; искомое вводит оператор. Выделение делается РАЗБИЕНИЕМ строки на куски, каждый из которых Razor
/// выводит как текст (экранируя), — а не вставкой разметки в строку и выводом через <c>MarkupString</c>:
/// иначе фрагмент вида «&lt;img onerror=…&gt;» исполнился бы в браузере следователя (XSS в circuit'е с
/// доступом к материалам дела).
/// Совпадение — как у поиска на сервере: подстрока без учёта регистра (<see cref="StringComparison.OrdinalIgnoreCase"/>;
/// посимвольно, поэтому длина совпадения равна длине искомого, в т.ч. для киргизских ү, ө, ң), искомое
/// обрезается по краям пробелов.
/// </remarks>
public static class TranscriptHighlight
{
    /// <summary>
    /// Разбивает <paramref name="text"/> на куски по всем вхождениям <paramref name="query"/> без учёта регистра.
    /// Пустое искомое или текст без вхождений — один кусок «не совпало».
    /// </summary>
    /// <param name="text">Текст фрагмента.</param>
    /// <param name="query">Искомое, как его ввёл оператор (обрезается).</param>
    /// <returns>Куски по порядку; склеенные, они дают исходный текст без изменений.</returns>
    public static IReadOnlyList<HighlightPart> Split(string? text, string? query)
    {
        var source = text ?? string.Empty;
        var needle = query?.Trim() ?? string.Empty;
        if (source.Length == 0)
        {
            return [];
        }

        if (needle.Length == 0)
        {
            return [new HighlightPart(source, false)];
        }

        var parts = new List<HighlightPart>();
        var position = 0;
        while (position < source.Length)
        {
            var found = source.IndexOf(needle, position, StringComparison.OrdinalIgnoreCase);
            if (found < 0)
            {
                break;
            }

            if (found > position)
            {
                parts.Add(new HighlightPart(source[position..found], false));
            }

            parts.Add(new HighlightPart(source.Substring(found, needle.Length), true));
            position = found + needle.Length;
        }

        if (position < source.Length)
        {
            parts.Add(new HighlightPart(source[position..], false));
        }

        return parts;
    }
}
