using System;
using System.Collections.Generic;
using System.Linq;
using ISC.AI.Profile.Investigation.Domain.Services;
using ISC.AI.Profile.Investigation.Domain.Enums;

namespace ISC.AI.Profile.Investigation.UI;

/// <summary>
/// Состояние страницы «Поиск» (ADR-0048) в адресе: запрос, виды и отдел — «Назад» из карточки возвращает к той же
/// выдаче. Подставленное в адрес значение ничего не открывает (поиск — в пределах дел субъекта, ТБ-071), неразобранное —
/// просто не учитывается.
/// </summary>
/// <param name="Text">Запрос; пусто — поиска ещё не было.</param>
/// <param name="Kinds">Виды выдачи.</param>
/// <param name="Direction">Отдел ОН/ОУ/ОТМ или все.</param>
public sealed record SearchPageState(string? Text = null, SearchKinds Kinds = SearchKinds.All, CaseDirection? Direction = null)
{
    private const string TextName = "q";
    private const string KindsName = "kinds";

    /// <summary>Виды выдачи по порядку разделов страницы.</summary>
    public static IReadOnlyList<SearchKinds> AllKinds { get; } =
    [
        SearchKinds.Cases, SearchKinds.Persons, SearchKinds.Vehicles, SearchKinds.Addresses,
        SearchKinds.Reports, SearchKinds.Transcripts, SearchKinds.Documents,
    ];

    /// <summary>Состояние по параметрам адреса; неразобранное — умолчание.</summary>
    public static SearchPageState Parse(IReadOnlyDictionary<string, string?> query)
    {
        ArgumentNullException.ThrowIfNull(query);

        string? Get(string name) => query.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

        var kinds = SearchKinds.None;
        foreach (var part in (Get(KindsName) ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // Только имена видов («Persons»): число («1») или «All» в адресе — не вид, а подстановка.
            var kind = AllKinds.FirstOrDefault(k => string.Equals(k.ToString(), part, StringComparison.OrdinalIgnoreCase));
            kinds |= kind;
        }

        return new SearchPageState(
            Get(TextName),
            kinds == SearchKinds.None ? SearchKinds.All : kinds,
            DirectionQuery.Parse(Get(DirectionQuery.Name)));
    }

    /// <summary>Адрес страницы с этим состоянием; умолчания в адрес не пишутся.</summary>
    public string ToUri()
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(Text))
        {
            parts.Add(TextName + "=" + Uri.EscapeDataString(Text.Trim()));
        }

        var kinds = Kinds & SearchKinds.All;
        if (kinds != SearchKinds.All && kinds != SearchKinds.None)
        {
            parts.Add(KindsName + "=" + string.Join(',', AllKinds.Where(k => (kinds & k) != 0)));
        }

        if (Direction is { } direction)
        {
            parts.Add(DirectionQuery.Name + "=" + DirectionQuery.Code(direction));
        }

        return parts.Count == 0 ? "/search" : "/search?" + string.Join('&', parts);
    }

    /// <summary>Название раздела выдачи.</summary>
    public static string Label(SearchKinds kind) => kind switch
    {
        SearchKinds.Cases => "Дела",
        SearchKinds.Persons => "Фигуранты",
        SearchKinds.Vehicles => "Автотранспорт",
        SearchKinds.Addresses => "Адреса",
        SearchKinds.Reports => "Сводки и справки",
        SearchKinds.Transcripts => "Расшифровки",
        SearchKinds.Documents => "Документы дел",
        _ => kind.ToString(),
    };
}

/// <summary>Кусок строки выдачи: совпал ли он с запросом.</summary>
/// <param name="Text">Кусок как есть (Razor экранирует его при выводе).</param>
/// <param name="IsMatch">Совпал с запросом и выделяется.</param>
public sealed record SearchHighlightPart(string Text, bool IsMatch);

/// <summary>
/// Выделение совпадения в строке выдачи единого поиска (ADR-0048) — по тому же правилу, что сам поиск: без учёта
/// регистра и «ё/е» (ТО-мат-11). Разбиение строки на куски, а не вставка разметки: текст строк — данные дел (в том
/// числе вывод модели расшифровки), и разметка из них исполняться не должна (XSS, как у выдачи расшифровок).
/// </summary>
public static class SearchHighlight
{
    /// <summary>Разбивает <paramref name="text"/> по вхождениям <paramref name="query"/>; склеенные куски дают исходный текст.</summary>
    /// <param name="text">Строка выдачи.</param>
    /// <param name="query">Запрос как ввели (обрезается, пробелы внутри сжимаются).</param>
    public static IReadOnlyList<SearchHighlightPart> Split(string? text, string? query)
    {
        var source = text ?? string.Empty;
        if (source.Length == 0)
        {
            return [];
        }

        var needle = Fold(string.Join(' ', (query ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)));
        if (needle.Length == 0)
        {
            return [new SearchHighlightPart(source, false)];
        }

        var folded = Fold(source);
        var parts = new List<SearchHighlightPart>();
        var position = 0;
        while (position < source.Length)
        {
            var found = folded.IndexOf(needle, position, StringComparison.Ordinal);
            if (found < 0)
            {
                break;
            }

            if (found > position)
            {
                parts.Add(new SearchHighlightPart(source[position..found], false));
            }

            parts.Add(new SearchHighlightPart(source.Substring(found, needle.Length), true));
            position = found + needle.Length;
        }

        if (position < source.Length)
        {
            parts.Add(new SearchHighlightPart(source[position..], false));
        }

        return parts;
    }

    // Посимвольно (длина не меняется — индексы совпадают с исходной строкой): нижний регистр и «ё» → «е».
    private static string Fold(string value) =>
        string.Create(value.Length, value, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                var lower = char.ToLowerInvariant(source[i]);
                span[i] = lower == 'ё' ? 'е' : lower;
            }
        });
}
