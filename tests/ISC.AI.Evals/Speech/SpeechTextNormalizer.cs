using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ISC.AI.Evals.Speech;

/// <summary>
/// Нормализация текста перед сравнением расшифровки с эталоном (КИ-10, ADR-0026). Применяется ОДИНАКОВО к
/// эталону и к выходу модели, поэтому метрика измеряет ошибки распознавания слов, а не разницу в оформлении.
/// </summary>
/// <remarks>
/// <para>Правила (по порядку):</para>
/// <list type="number">
/// <item>Юникод приводится к форме NFC: «е» + комбинируемая диереза становится «ё», «и» + бреве — «й».</item>
/// <item>Нижний регистр по инвариантной культуре (заглавные <c>Ң Ө Ү</c> → <c>ң ө ү</c>).</item>
/// <item><c>ё</c> → <c>е</c>: в эталонах её пишут непоследовательно, модель — как обучена.</item>
/// <item>Знаки препинания, символы и любые пробельные символы — граница слова (дефис тоже: «что-то» →
/// «что то», «үй-бүлө» → «үй бүлө»); подряд идущие границы схлопываются в один пробел.</item>
/// <item>Оставшиеся после NFC комбинируемые знаки (ударение «замо́к») и невидимые символы форматирования
/// (мягкий перенос, нулевой пробел, BOM) удаляются без разрыва слова.</item>
/// </list>
/// <para>ЧЕГО НОРМАЛИЗАЦИЯ НАМЕРЕННО НЕ ДЕЛАЕТ.</para>
/// <list type="bullet">
/// <item><b>Киргизские буквы <c>ң ө ү</c> сохраняются как есть</b> и НЕ приравниваются к <c>н о у</c>: это
/// отдельные буквы, различающие слова («үй» — дом, «уй» — корова). Модель, пишущая «уйдо» вместо «үйдө»,
/// ошибается, и метрика обязана это показать.</item>
/// <item><b>Числа не нормализуются</b>: цифры остаются цифрами, числительные словами — словами. Эталон
/// пишется по методике (см. <c>Speech/README.md</c>): GigaAM выдаёт только буквы, поэтому числа в эталоне
/// записываются словами так, как их произнесли; «25» в эталоне против «двадцать пять» у модели — ошибка
/// эталона, а не модели (<see cref="ReferenceTextLint"/> о таком предупреждает).</item>
/// <item>Латинские двойники киргизских букв (<c>ö ü ñ</c>) не подменяются молча — о них тоже
/// предупреждает <see cref="ReferenceTextLint"/>: эталон исправляет человек.</item>
/// </list>
/// <para>ПОМЕТКА <c>[неразборчиво]</c> (методика пилота, 4.6) — только в ЭТАЛОНЕ: <see cref="NormalizeReference"/>,
/// <see cref="ReferenceWords"/> и <see cref="ReferenceCharacters"/> сохраняют её отдельной единицей, а
/// <see cref="ErrorRateCalculator"/> выравнивает её с любым (в том числе нулевым) числом слов модели без
/// штрафа. Выход модели разбирается <see cref="Normalize"/> как обычный текст: скобки в нём — границы слов.</para>
/// </remarks>
public static partial class SpeechTextNormalizer
{
    /// <summary>
    /// Пометка неразборчивого участка в эталоне: одна пометка на один непрерывный участок, сколько бы слов в
    /// нём ни было. Распознаётся без учёта регистра и пробелов внутри скобок (<c>[ Неразборчиво ]</c>).
    /// </summary>
    public const string UnintelligibleMarker = "[неразборчиво]";

    private const int SmallYo = 'ё';
    private const int SmallYe = 'е';

    /// <summary>Нормализует текст по правилам класса; пустой или <see langword="null"/> — пустая строка.</summary>
    /// <param name="text">Эталон или выход модели.</param>
    /// <returns>Слова через одиночный пробел, без пробелов в начале и в конце.</returns>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var composed = text.Normalize(NormalizationForm.FormC);
        var builder = new StringBuilder(composed.Length);
        var needSeparator = false;
        Span<char> buffer = stackalloc char[2];
        foreach (var rune in composed.EnumerateRunes())
        {
            switch (Classify(rune))
            {
                case CharacterRole.WordCharacter:
                    if (needSeparator && builder.Length > 0)
                    {
                        builder.Append(' ');
                    }

                    needSeparator = false;
                    var lower = Rune.ToLowerInvariant(rune);
                    if (lower.Value == SmallYo)
                    {
                        lower = new Rune(SmallYe);
                    }

                    var written = lower.EncodeToUtf16(buffer);
                    builder.Append(buffer[..written]);
                    break;

                case CharacterRole.Removed:
                    // Ударение и невидимые символы — часть слова, а не граница: удаляются без пробела.
                    break;

                default:
                    needSeparator = true;
                    break;
            }
        }

        return builder.ToString();
    }

    /// <summary>Слова нормализованного текста — единицы подсчёта WER.</summary>
    /// <param name="text">Эталон или выход модели.</param>
    public static IReadOnlyList<string> Words(string? text)
    {
        var normalized = Normalize(text);
        return normalized.Length == 0 ? [] : normalized.Split(' ');
    }

    /// <summary>
    /// Символы нормализованного текста (кодовые точки Юникода, включая одиночные пробелы между словами) —
    /// единицы подсчёта CER, как в jiwer/sclite.
    /// </summary>
    /// <param name="text">Эталон или выход модели.</param>
    public static int[] Characters(string? text)
    {
        var normalized = Normalize(text);
        var runes = new List<int>(normalized.Length);
        foreach (var rune in normalized.EnumerateRunes())
        {
            runes.Add(rune.Value);
        }

        return [.. runes];
    }

    /// <summary>
    /// Нормализует ЭТАЛОН: как <see cref="Normalize"/>, но пометки <see cref="UnintelligibleMarker"/>
    /// сохраняются отдельными словами (в нормализованном тексте скобок больше нигде нет, поэтому пометку ни с
    /// чем не спутать). Нужен для файлов «по слову в строке» и для отчёта.
    /// </summary>
    /// <param name="text">Эталонная расшифровка как есть.</param>
    public static string NormalizeReference(string? text) => string.Join(' ', ReferenceWords(text));

    /// <summary>
    /// Слова эталона для WER: нормализованные слова и пометки <see cref="UnintelligibleMarker"/> на своих
    /// местах (пометки подряд не склеиваются — выравнивание от этого не меняется).
    /// </summary>
    /// <param name="text">Эталонная расшифровка как есть.</param>
    public static IReadOnlyList<string> ReferenceWords(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var composed = text.Normalize(NormalizationForm.FormC);
        var words = new List<string>();
        var position = 0;
        foreach (Match match in UnintelligiblePattern().Matches(composed))
        {
            words.AddRange(Words(composed[position..match.Index]));
            words.Add(UnintelligibleMarker);
            position = match.Index + match.Length;
        }

        words.AddRange(Words(composed[position..]));
        return words;
    }

    /// <summary>
    /// Символы эталона для CER: кодовые точки нормализованного текста с пробелами между словами, а на месте
    /// пометки — <see cref="ErrorRateCalculator.Wildcard"/>. Пробелы ВОКРУГ пометки не пишутся: участок
    /// поглощает и границы слов рядом с собой, иначе «мама [неразборчиво] раму» против «мама раму» дало бы
    /// ошибку в лишнем пробеле, которой модель не делала. Соседние слова эталона при этом к участку не
    /// «приклеиваются»: <see cref="ErrorRateCalculator.CharacterErrors"/> разрешает ему поглощать только целые
    /// слова модели (от пробела или края текста до пробела или края), поэтому лишние буквы на стыке с соседним
    /// словом («үй [неразборчиво]» против «үйгө бар») — ошибки.
    /// </summary>
    /// <param name="text">Эталонная расшифровка как есть.</param>
    public static int[] ReferenceCharacters(string? text)
    {
        var units = new List<int>();
        var previousIsWord = false;
        foreach (var word in ReferenceWords(text))
        {
            if (IsUnintelligible(word))
            {
                units.Add(ErrorRateCalculator.Wildcard);
                previousIsWord = false;
                continue;
            }

            if (previousIsWord)
            {
                units.Add(' ');
            }

            foreach (var rune in word.EnumerateRunes())
            {
                units.Add(rune.Value);
            }

            previousIsWord = true;
        }

        return [.. units];
    }

    /// <summary>Слово эталона — пометка <see cref="UnintelligibleMarker"/>.</summary>
    /// <param name="word">Слово из <see cref="ReferenceWords"/>.</param>
    public static bool IsUnintelligible(string? word) => string.Equals(word, UnintelligibleMarker, StringComparison.Ordinal);

    /// <summary>Сколько пометок <see cref="UnintelligibleMarker"/> в эталоне.</summary>
    /// <param name="text">Эталонная расшифровка как есть.</param>
    public static int CountUnintelligible(string? text) =>
        string.IsNullOrEmpty(text) ? 0 : UnintelligiblePattern().Count(text.Normalize(NormalizationForm.FormC));

    /// <summary>Сколько слов эталона оцениваются (без пометок <see cref="UnintelligibleMarker"/>).</summary>
    /// <param name="text">Эталонная расшифровка как есть.</param>
    public static int CountScoredReferenceWords(string? text)
    {
        var count = 0;
        foreach (var word in ReferenceWords(text))
        {
            if (!IsUnintelligible(word))
            {
                count++;
            }
        }

        return count;
    }

    [GeneratedRegex(@"\[\s*неразборчиво\s*\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UnintelligiblePattern();

    private static CharacterRole Classify(Rune rune) => Rune.GetUnicodeCategory(rune) switch
    {
        UnicodeCategory.UppercaseLetter
            or UnicodeCategory.LowercaseLetter
            or UnicodeCategory.TitlecaseLetter
            or UnicodeCategory.ModifierLetter
            or UnicodeCategory.OtherLetter
            or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.DecimalDigitNumber
            or UnicodeCategory.LetterNumber
            or UnicodeCategory.OtherNumber => CharacterRole.WordCharacter,
        UnicodeCategory.NonSpacingMark
            or UnicodeCategory.EnclosingMark
            or UnicodeCategory.Format => CharacterRole.Removed,
        _ => CharacterRole.Separator,
    };

    private enum CharacterRole
    {
        Separator,
        WordCharacter,
        Removed,
    }
}
