using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ISC.AI.Evals.Speech;

/// <summary>
/// Проверка эталонной расшифровки на типичные ошибки оформления, которые исказили бы WER/CER не по вине
/// модели. Итог — предупреждения в отчёте, а не отказ: эталон исправляет человек, молча текст не меняется.
/// Правила оформления эталона — методика пилота (<c>docs/следствие/Пилот_расшифровки_речи.md</c>, раздел 4).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Цифры.</b> GigaAM выдаёт только буквы: «25» в эталоне против «двадцать пять» у модели — два-три
/// «ошибочных» слова. По методике числа в эталоне пишутся словами, как произнесены (нормализация чисел
/// намеренно не делается — см. <see cref="SpeechTextNormalizer"/>).</item>
/// <item><b>Латинские двойники киргизских букв</b> (<c>ö ü ñ</c>, <c>ɵ</c>, алтайские <c>ҥ ӧ ӱ</c>) вместо
/// <c>ө ү ң</c>: так набирают без киргизской раскладки; модель пишет кириллицу, и слово засчиталось бы
/// ошибкой.</item>
/// <item><b>Смесь латиницы и кириллицы в одном слове</b> («cобака» с латинской c) — опечатка раскладки.</item>
/// <item><b>Пометки в квадратных скобках, кроме <c>[неразборчиво]</c></b> (<c>[шум]</c>, <c>[смех]</c>, опечатка
/// <c>[неразборчево]</c>): методика других пометок не предусматривает, и их текст стал бы словами эталона.</item>
/// <item><b>Эталон целиком из пометок <c>[неразборчиво]</c></b> — оценивать нечего: запись не влияет на WER.</item>
/// </list>
/// Пустой эталон здесь не проверяется: пустой эталон речевой записи — ошибка набора
/// (<see cref="SpeechEvalDataset.LoadAsync"/>), а у контрольного файла без речи он пуст по определению.
/// </remarks>
public static partial class ReferenceTextLint
{
    private const int MaxExamples = 3;

    private static readonly Dictionary<int, string> KyrgyzLookalikes = new()
    {
        ['ö'] = "ө",
        ['ü'] = "ү",
        ['ñ'] = "ң",
        ['ɵ'] = "ө",
        ['ҥ'] = "ң",
        ['ӧ'] = "ө",
        ['ӱ'] = "ү",
    };

    /// <summary>Возвращает предупреждения по тексту эталона (пусто — замечаний нет).</summary>
    /// <param name="referenceText">Текст эталона как есть (до нормализации).</param>
    public static IReadOnlyList<string> Check(string? referenceText)
    {
        var warnings = new List<string>();
        var tokens = SpeechTextNormalizer.ReferenceWords(referenceText);
        var words = tokens.Where(token => !SpeechTextNormalizer.IsUnintelligible(token)).ToList();

        var unknownMarks = BracketPattern().Matches(referenceText?.Normalize(NormalizationForm.FormC) ?? string.Empty)
            .Select(match => match.Value)
            .Where(mark => SpeechTextNormalizer.CountUnintelligible(mark) == 0)
            .ToList();
        if (unknownMarks.Count > 0)
        {
            warnings.Add(
                $"пометки в скобках {Examples(unknownMarks)} ({Count(unknownMarks.Count)} шт.) не предусмотрены методикой (4.6): их текст считается "
                + $"словами эталона — неразборчивое отмечается только {SpeechTextNormalizer.UnintelligibleMarker}, шумы и смех не отмечаются");
        }

        if (words.Count == 0)
        {
            if (tokens.Count > 0)
            {
                warnings.Add($"эталон состоит только из пометок {SpeechTextNormalizer.UnintelligibleMarker} — оцениваемых слов нет, запись не влияет на WER");
            }

            return warnings;
        }

        var withDigits = words.Where(word => word.Any(char.IsDigit)).ToList();
        if (withDigits.Count > 0)
        {
            warnings.Add(
                $"числа цифрами ({Count(withDigits.Count)} сл., например {Examples(withDigits)}): модель пишет числа словами — "
                + "по методике их записывают словами, как произнесены, иначе каждое число засчитывается ошибкой");
        }

        var lookalikes = new SortedDictionary<int, int>();
        foreach (var rune in string.Concat(words).EnumerateRunes())
        {
            if (KyrgyzLookalikes.ContainsKey(rune.Value))
            {
                lookalikes[rune.Value] = lookalikes.GetValueOrDefault(rune.Value) + 1;
            }
        }

        foreach (var (code, count) in lookalikes)
        {
            warnings.Add(
                $"символ «{char.ConvertFromUtf32(code)}» (U+{code.ToString("X4", CultureInfo.InvariantCulture)}) встречается {Count(count)} раз — "
                + $"вероятно, вместо киргизской «{KyrgyzLookalikes[code]}»; модель пишет кириллицу, такое слово засчитается ошибкой");
        }

        var mixedScript = words.Where(IsMixedScript).ToList();
        if (mixedScript.Count > 0)
        {
            warnings.Add(
                $"{Count(mixedScript.Count)} сл. смешивают латиницу и кириллицу (например {Examples(mixedScript)}) — вероятно, опечатка раскладки");
        }

        return warnings;
    }

    private static bool IsMixedScript(string word)
    {
        var hasLatin = false;
        var hasCyrillic = false;
        foreach (var rune in word.EnumerateRunes())
        {
            if (!Rune.IsLetter(rune))
            {
                continue;
            }

            // Латиница: блоки Basic Latin … Latin Extended-B и IPA (там же «ɵ»); кириллица: U+0400–U+052F.
            hasLatin |= rune.Value <= 0x02AF;
            hasCyrillic |= rune.Value is >= 0x0400 and <= 0x052F;
        }

        return hasLatin && hasCyrillic;
    }

    private static string Examples(IEnumerable<string> words) =>
        string.Join(", ", words.Distinct(StringComparer.Ordinal).Take(MaxExamples).Select(word => $"«{word}»"));

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);

    // Любая пометка в квадратных скобках в пределах строки; допустимая [неразборчиво] отсеивается после.
    [GeneratedRegex(@"\[[^\[\]\r\n]*\]", RegexOptions.CultureInvariant)]
    private static partial Regex BracketPattern();
}
