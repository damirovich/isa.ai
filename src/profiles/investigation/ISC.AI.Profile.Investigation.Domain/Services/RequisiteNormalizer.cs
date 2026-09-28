using System.Globalization;
using System.Text;

namespace ISC.AI.Profile.Investigation.Domain.Services;

/// <summary>
/// Нормализация реквизитов для пересечений, архива и поиска (ТО-мат-11, ТФ-ПЕР-07): разные написания одного
/// значения приводятся к одному ключу, а сравнение затем идёт ТОЧНО по ключу. Нечёткого сравнения здесь нет
/// намеренно — оно только по решению Заказчика (Приложение В ТЗ, вопрос 1). Исходное значение хранится рядом.
/// </summary>
/// <remarks>
/// Правила — детерминированные и одинаковые для записи и поиска: ключ, посчитанный сегодня, обязан совпасть
/// с ключом, посчитанным при следующей правке. Поэтому менять правила — значит пересчитывать ключи всех
/// строк миграцией. Киргизские буквы ө, ү, ң сохраняются; «ё» приравнивается к «е».
/// </remarks>
public static class RequisiteNormalizer
{
    // Кириллические буквы, совпадающие начертанием с латинскими: в госномере «А123ВС» и «A123BC» — один номер.
    private static readonly Dictionary<char, char> PlateHomoglyphs = new()
    {
        ['А'] = 'A', ['В'] = 'B', ['Е'] = 'E', ['К'] = 'K', ['М'] = 'M', ['Н'] = 'H',
        ['О'] = 'O', ['Р'] = 'P', ['С'] = 'C', ['Т'] = 'T', ['У'] = 'Y', ['Х'] = 'X',
    };

    // Сокращения типов адресных объектов — к одному виду (русские и киргизские формы).
    private static readonly Dictionary<string, string> AddressMarkers = new(StringComparer.Ordinal)
    {
        ["улица"] = "ул", ["ул"] = "ул", ["көчөсү"] = "ул", ["көчө"] = "ул",
        ["проспект"] = "пр", ["пр"] = "пр", ["прт"] = "пр", ["просп"] = "пр",
        ["микрорайон"] = "мкр", ["мкр"] = "мкр", ["мкрн"] = "мкр", ["мн"] = "мкр", ["кичирайон"] = "мкр",
        ["переулок"] = "пер", ["пер"] = "пер",
        ["бульвар"] = "бул", ["бул"] = "бул", ["бульв"] = "бул",
        ["жилмассив"] = "жм", ["жм"] = "жм", ["ж/м"] = "жм",
        ["квартира"] = "кв", ["кв"] = "кв", ["батир"] = "кв",
        ["корпус"] = "корп", ["корп"] = "корп",
        ["строение"] = "стр", ["стр"] = "стр",
        ["область"] = "обл", ["обл"] = "обл", ["облусу"] = "обл",
        ["район"] = "рн", ["рн"] = "рн", ["району"] = "рн",
        ["село"] = "с", ["с"] = "с", ["айылы"] = "с",
    };

    // Метки, которые пишут то с ними, то без них, — выбрасываются: «г. Бишкек, д. 1» = «Бишкек, 1».
    private static readonly HashSet<string> DroppedAddressMarkers = new(StringComparer.Ordinal)
    {
        "г", "город", "шаары", "д", "дом", "үй",
    };

    /// <summary>
    /// Госномер: только буквы и цифры (пробелы, дефисы, точки выбрасываются), верхний регистр, кириллические
    /// омоглифы → латиница. «01 kg 123 авс» и «01KG123ABC» дают один ключ. Пусто — <see langword="null"/>.
    /// </summary>
    public static string? Plate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var c in value.Normalize(NormalizationForm.FormKC).ToUpperInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(PlateHomoglyphs.TryGetValue(c, out var latin) ? latin : c);
            }
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    /// <summary>
    /// ФИО: слова в нижнем регистре, «ё» → «е», знаки препинания выбрасываются, слова упорядочены — «Иванов
    /// Иван» и «иван ИВАНОВ» дают один ключ. Пусто — <see langword="null"/>.
    /// </summary>
    public static string? PersonName(string? value)
    {
        var words = Words(value, keepSlash: false);
        if (words.Count == 0)
        {
            return null;
        }

        words.Sort(StringComparer.Ordinal);
        return string.Join(' ', words);
    }

    /// <summary>
    /// Адрес: слова в нижнем регистре, «ё» → «е», дефисы внутри слов снимаются («Кара-Балта» = «Карабалта»),
    /// прочие знаки — разделители, кроме «/» (номер «5/1» сохраняется); типы объектов сокращаются к одному
    /// виду («улица» = «ул.»), метки города и дома выбрасываются. Порядок слов сохраняется: в адресе он
    /// значим. Пусто — <see langword="null"/>.
    /// </summary>
    public static string? Address(string? value)
    {
        var words = Words(value, keepSlash: true);
        var result = new List<string>(words.Count);
        foreach (var word in words)
        {
            if (DroppedAddressMarkers.Contains(word))
            {
                continue;
            }

            result.Add(AddressMarkers.TryGetValue(word, out var marker) ? marker : word);
        }

        return result.Count == 0 ? null : string.Join(' ', result);
    }

    /// <summary>
    /// Слова значения: NFKC, нижний регистр (инвариантный), «ё» → «е», дефисы и апострофы склеивают слово,
    /// «/» — часть слова при <paramref name="keepSlash"/>, остальные не буквы и не цифры — разделители.
    /// </summary>
    private static List<string> Words(string? value, bool keepSlash)
    {
        var words = new List<string>();
        if (string.IsNullOrWhiteSpace(value))
        {
            return words;
        }

        var current = new StringBuilder();
        foreach (var raw in value.Normalize(NormalizationForm.FormKC).ToLower(CultureInfo.InvariantCulture))
        {
            var c = raw == 'ё' ? 'е' : raw;
            if (char.IsLetterOrDigit(c) || (keepSlash && c == '/'))
            {
                current.Append(c);
            }
            else if (c is '-' or '‐' or '‑' or '–' or '\'' or '’' or '`')
            {
                // Дефис и апостроф — внутри слова: «Кара-Балта», «Д'Артаньян». Не разделитель и не символ ключа.
            }
            else if (current.Length > 0)
            {
                AddWord(words, current);
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            AddWord(words, current);
        }

        return words;
    }

    // Слово без букв и цифр (одиночная «/» между улицами) ключом не является.
    private static void AddWord(List<string> words, StringBuilder current)
    {
        var word = current.ToString();
        if (word.Any(char.IsLetterOrDigit))
        {
            words.Add(word);
        }
    }
}
