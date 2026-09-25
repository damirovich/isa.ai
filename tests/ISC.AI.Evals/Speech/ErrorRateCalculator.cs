using System;
using System.Collections.Generic;
using System.Threading;

namespace ISC.AI.Evals.Speech;

/// <summary>
/// Расчёт WER и CER (КИ-10, ADR-0026): расстояние Левенштейна между эталоном и выходом модели по словам и по
/// символам, с разбиением на замены, удаления и вставки. Оба текста предварительно проходят
/// <see cref="SpeechTextNormalizer"/>.
/// </summary>
/// <remarks>
/// <para>WER = (S + D + I) / N_слов, CER = (S + D + I) / N_символов (символы — кодовые точки нормализованного
/// текста вместе с пробелами между словами, как в jiwer/sclite). Обе метрики могут превышать 1.</para>
/// <para>УЧАСТКИ <c>[неразборчиво]</c> (методика пилота, 4.6). Пометка в эталоне — «джокер» <see cref="Wildcard"/>:
/// в динамическом программировании её строка выравнивается с ЛЮБЫМ, в том числе пустым, отрезком гипотезы
/// бесплатно (переход «вниз» и «вправо» по ней стоит 0). Поэтому слова модели на месте неразборчивого участка
/// не считаются ни вставками, ни заменами, а сама пометка не входит в N. Без этого WER записей с пометками
/// был бы завышен: модель, возможно, расслышала верно, а человек — нет.</para>
/// <para>ДЖОКЕР В CER ПОГЛОЩАЕТ ТОЛЬКО ЦЕЛЫЕ СЛОВА. По символам участок совмещается с непустым отрезком
/// выхода модели, только если отрезок начинается и кончается на границе слова (пробел или край текста);
/// пустой отрезок допустим где угодно. Иначе буквы, приклеенные к соседнему слову без пробела, уходили бы в
/// участок бесплатно: при эталоне «үй [неразборчиво]» и выходе «үйгө бар» неверный аффикс «гө» — обычная
/// ошибка агглютинативного киргизского — дал бы CER 0 при WER 100 %. Пробелы вокруг участка по-прежнему не
/// оцениваются (их нет в N), поэтому склеенные моделью соседние слова ошибкой не считаются.</para>
/// <para>ПАМЯТЬ И ВРЕМЯ. Динамическое программирование хранит две строки таблицы, а не всю таблицу: запись
/// допроса на 1–2 часа — это до 15–20 тысяч слов и около 100 тысяч символов, полная таблица символов заняла
/// бы десятки гигабайт. Время — O(N·M) после отсечения общих начала и конца: замер 25.09.2026 на часовом
/// тексте (~49 000 символов) — CER около 5 с в Release-сборке и около 12 с в Debug, WER — доли секунды.
/// Это заметно меньше самого распознавания. Отмена проверяется на каждой строке таблицы.</para>
/// </remarks>
public static class ErrorRateCalculator
{
    /// <summary>
    /// Код участка <c>[неразборчиво]</c> в эталоне для <see cref="CountEdits(ReadOnlySpan{int}, ReadOnlySpan{int}, CancellationToken)"/>:
    /// выравнивается с любым числом единиц гипотезы без штрафа и не входит в длину эталона. Коды слов и
    /// кодовые точки символов неотрицательны, поэтому с ними он не совпадает. В гипотезе этот код — обычная
    /// единица.
    /// </summary>
    public const int Wildcard = -1;

    /// <summary>Ошибки по словам (WER): тексты нормализуются и делятся на слова.</summary>
    /// <param name="reference">
    /// Эталонная расшифровка (как её записал человек); пометки <c>[неразборчиво]</c> — участки без оценки.
    /// </param>
    /// <param name="hypothesis">Выход модели.</param>
    /// <param name="cancellationToken">Отмена длинного расчёта.</param>
    public static EditCounts WordErrors(string? reference, string? hypothesis, CancellationToken cancellationToken = default)
    {
        var codes = new Dictionary<string, int>(StringComparer.Ordinal);
        var referenceCodes = Encode(SpeechTextNormalizer.ReferenceWords(reference), codes, SpeechTextNormalizer.IsUnintelligible);

        // Скобки в выходе модели — границы слов, поэтому пометкой её слово быть не может: джокеров в гипотезе нет.
        var hypothesisCodes = Encode(SpeechTextNormalizer.Words(hypothesis), codes, isWildcard: null);
        return CountEdits(referenceCodes, hypothesisCodes, cancellationToken);
    }

    /// <summary>
    /// Ошибки по символам (CER): тексты нормализуются, считаются кодовые точки вместе с пробелами. Участок
    /// <c>[неразборчиво]</c> поглощает только целые слова модели (см. описание класса).
    /// </summary>
    /// <param name="reference">Эталонная расшифровка; пометки <c>[неразборчиво]</c> — участки без оценки.</param>
    /// <param name="hypothesis">Выход модели.</param>
    /// <param name="cancellationToken">Отмена длинного расчёта.</param>
    public static EditCounts CharacterErrors(string? reference, string? hypothesis, CancellationToken cancellationToken = default) =>
        Align(
            SpeechTextNormalizer.ReferenceCharacters(reference),
            SpeechTextNormalizer.Characters(hypothesis),
            wordSeparator: ' ',
            cancellationToken);

    /// <summary>
    /// Выравнивание произвольных последовательностей единиц (слов, символов): каждая различная единица
    /// получает числовой код, дальше считается <see cref="CountEdits(ReadOnlySpan{int}, ReadOnlySpan{int}, CancellationToken)"/>.
    /// Участков без оценки здесь нет — их знают только <see cref="WordErrors"/> и <see cref="CharacterErrors"/>.
    /// </summary>
    /// <typeparam name="T">Тип единицы.</typeparam>
    /// <param name="reference">Эталон.</param>
    /// <param name="hypothesis">Гипотеза.</param>
    /// <param name="comparer">Равенство единиц; по умолчанию — стандартное для <typeparamref name="T"/>.</param>
    /// <param name="cancellationToken">Отмена длинного расчёта.</param>
    public static EditCounts CountEdits<T>(
        IReadOnlyList<T> reference,
        IReadOnlyList<T> hypothesis,
        IEqualityComparer<T>? comparer = null,
        CancellationToken cancellationToken = default)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(hypothesis);

        var codes = new Dictionary<T, int>(comparer ?? EqualityComparer<T>.Default);
        return CountEdits(Encode(reference, codes, isWildcard: null), Encode(hypothesis, codes, isWildcard: null), cancellationToken);
    }

    /// <summary>
    /// Расстояние Левенштейна между последовательностями кодов с разбиением на замены, удаления и вставки.
    /// Каждая операция стоит 1; при равной стоимости предпочитается диагональ (совпадение или замена),
    /// затем удаление, затем вставка — итоговое число ошибок от этого не зависит. Код <see cref="Wildcard"/>
    /// в эталоне — участок без оценки (см. описание класса).
    /// </summary>
    /// <param name="reference">Эталон.</param>
    /// <param name="hypothesis">Гипотеза.</param>
    /// <param name="cancellationToken">Отмена длинного расчёта.</param>
    public static EditCounts CountEdits(ReadOnlySpan<int> reference, ReadOnlySpan<int> hypothesis, CancellationToken cancellationToken = default) =>
        Align(reference, hypothesis, wordSeparator: null, cancellationToken);

    /// <summary>
    /// То же, что <see cref="CountEdits(ReadOnlySpan{int}, ReadOnlySpan{int}, CancellationToken)"/>, но джокер
    /// поглощает только целые слова гипотезы: непустой отрезок, который он совмещает, начинается и кончается
    /// на границе слова — у единицы <paramref name="wordSeparator"/> или на краю гипотезы. Пустой отрезок
    /// допустим в любом месте. Так считается CER (<see cref="CharacterErrors"/>), разделитель — пробел.
    /// </summary>
    /// <param name="reference">Эталон.</param>
    /// <param name="hypothesis">Гипотеза.</param>
    /// <param name="wordSeparator">Код единицы-разделителя слов в гипотезе.</param>
    /// <param name="cancellationToken">Отмена длинного расчёта.</param>
    public static EditCounts CountEdits(
        ReadOnlySpan<int> reference, ReadOnlySpan<int> hypothesis, int wordSeparator, CancellationToken cancellationToken = default) =>
        Align(reference, hypothesis, wordSeparator, cancellationToken);

    /// <summary>Выравнивание с джокером; <paramref name="wordSeparator"/> = <see langword="null"/> — границей слова считается любое место.</summary>
    private static EditCounts Align(
        ReadOnlySpan<int> reference, ReadOnlySpan<int> hypothesis, int? wordSeparator, CancellationToken cancellationToken)
    {
        var referenceLength = reference.Length - reference.Count(Wildcard);
        var hypothesisLength = hypothesis.Length;

        // Общие начало и конец — всегда совпадения в некотором оптимальном выравнивании; их отсечение
        // резко сокращает таблицу на хорошо распознанных записях. Отсечение останавливается на первом (и
        // последнем) джокере: его совпадение по коду с единицей гипотезы ничего не значит. Если джокер
        // поглощает только целые слова, отсечение к тому же отступает до границы слова гипотезы: иначе
        // оно навязало бы совпадение половины слова и запретило джокеру выравнивание, которое дешевле
        // (эталон «а [неразборчиво]» против «абвгд е»: пропуск «а» и поглощение всего — 1 ошибка, а не 4).
        var prefix = reference.CommonPrefixLength(hypothesis);
        var firstWildcard = reference.IndexOf(Wildcard);
        if (firstWildcard >= 0)
        {
            prefix = Math.Min(prefix, firstWildcard);
            while (prefix > 0 && !IsWordBoundary(hypothesis, prefix, wordSeparator))
            {
                prefix--;
            }
        }

        var referenceTail = reference[prefix..];
        var hypothesisTail = hypothesis[prefix..];
        var suffix = CommonSuffixLength(referenceTail, hypothesisTail);
        var lastWildcard = referenceTail.LastIndexOf(Wildcard);
        if (lastWildcard >= 0)
        {
            suffix = Math.Min(suffix, referenceTail.Length - 1 - lastWildcard);
            while (suffix > 0 && !IsWordBoundary(hypothesis, hypothesis.Length - suffix, wordSeparator))
            {
                suffix--;
            }
        }

        var rest = referenceTail[..^suffix];
        var restHypothesis = hypothesisTail[..^suffix];

        if (rest.IsEmpty)
        {
            // Джокеров в остатке нет (отсечение на них останавливается) — вся оставшаяся гипотеза лишняя.
            return new EditCounts(referenceLength, hypothesisLength, 0, 0, restHypothesis.Length);
        }

        if (restHypothesis.IsEmpty)
        {
            return new EditCounts(referenceLength, hypothesisLength, 0, rest.Length - rest.Count(Wildcard), 0);
        }

        // Две строки таблицы. Клетка хранит стоимость лучшего пути в неё и число замен и удалений на нём
        // (вставки = стоимость − S − D: поглощённое джокером стоит 0 и в стоимость не входит). Левая и
        // диагональная клетки держатся в локальных переменных — на клетку одно чтение и одна запись в память.
        var columns = restHypothesis.Length + 1;
        var previous = new Cell[columns];
        var current = new Cell[columns];

        for (var j = 0; j < columns; j++)
        {
            previous[j] = new Cell(j, 0, 0); // пустой префикс эталона против j единиц гипотезы — j вставок
        }

        for (var i = 1; i <= rest.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var referenceUnit = rest[i - 1];
            if (referenceUnit == Wildcard)
            {
                // Строка джокера: участок бесплатно поглощает отрезок гипотезы [k, j): D[i][j] = min D[i−1][k].
                // Без разделителя слов k — любое ≤ j (минимум по префиксу строки). С разделителем непустой
                // отрезок обязан начинаться и кончаться на границе слова: на границе j берётся минимум по
                // границам k ≤ j, а посреди слова участок поглощает только пустой отрезок — D[i−1][j].
                // Границы проверяются по ПОЛНОЙ гипотезе (prefix + j): отсечённое начало — не край слова.
                var absorbed = default(Cell);
                var seenBoundary = false;
                for (var j = 0; j < columns; j++)
                {
                    var up = previous[j];
                    if (IsWordBoundary(hypothesis, prefix + j, wordSeparator))
                    {
                        absorbed = !seenBoundary || up.Cost <= absorbed.Cost ? up : absorbed;
                        seenBoundary = true;
                        current[j] = absorbed;
                    }
                    else
                    {
                        current[j] = up;
                    }
                }

                (previous, current) = (current, previous);
                continue;
            }

            // Первый столбец: префикс эталона против пустой гипотезы — удаления всех его оцениваемых единиц.
            var left = new Cell(previous[0].Cost + 1, 0, previous[0].Deletions + 1);
            current[0] = left;
            var diagonal = previous[0];

            for (var j = 1; j < columns; j++)
            {
                var up = previous[j];
                var mismatch = referenceUnit == restHypothesis[j - 1] ? 0 : 1;
                var diagonalCost = diagonal.Cost + mismatch;
                var deletionCost = up.Cost + 1;
                var insertionCost = left.Cost + 1;

                Cell next;
                if (diagonalCost <= deletionCost && diagonalCost <= insertionCost)
                {
                    next = new Cell(diagonalCost, diagonal.Substitutions + mismatch, diagonal.Deletions);
                }
                else if (deletionCost <= insertionCost)
                {
                    next = new Cell(deletionCost, up.Substitutions, up.Deletions + 1);
                }
                else
                {
                    next = new Cell(insertionCost, left.Substitutions, left.Deletions);
                }

                current[j] = next;
                left = next;
                diagonal = up;
            }

            (previous, current) = (current, previous);
        }

        var last = previous[columns - 1];
        var insertions = last.Cost - last.Substitutions - last.Deletions;
        var hits = referenceLength - last.Substitutions - last.Deletions;
        return new EditCounts(
            referenceLength,
            hypothesisLength,
            last.Substitutions,
            last.Deletions,
            insertions,
            Unscored: hypothesisLength - hits - last.Substitutions - insertions);
    }

    /// <summary>Клетка таблицы: стоимость лучшего пути и число замен и удалений на нём.</summary>
    private readonly record struct Cell(int Cost, int Substitutions, int Deletions);

    /// <summary>
    /// Место <paramref name="position"/> (между единицами гипотезы) — граница слова: край гипотезы или соседство
    /// с разделителем. Без разделителя (<see langword="null"/>, WER) граница — любое место.
    /// </summary>
    private static bool IsWordBoundary(ReadOnlySpan<int> hypothesis, int position, int? wordSeparator) =>
        wordSeparator is not { } separator
        || position == 0
        || position == hypothesis.Length
        || hypothesis[position - 1] == separator
        || hypothesis[position] == separator;

    private static int CommonSuffixLength(ReadOnlySpan<int> left, ReadOnlySpan<int> right)
    {
        var length = 0;
        var max = Math.Min(left.Length, right.Length);
        while (length < max && left[left.Length - 1 - length] == right[right.Length - 1 - length])
        {
            length++;
        }

        return length;
    }

    private static int[] Encode<T>(IReadOnlyList<T> units, Dictionary<T, int> codes, Func<T, bool>? isWildcard)
        where T : notnull
    {
        var encoded = new int[units.Count];
        for (var index = 0; index < encoded.Length; index++)
        {
            var unit = units[index];
            if (isWildcard is not null && isWildcard(unit))
            {
                encoded[index] = Wildcard;
                continue;
            }

            if (!codes.TryGetValue(unit, out var code))
            {
                code = codes.Count;
                codes.Add(unit, code);
            }

            encoded[index] = code;
        }

        return encoded;
    }
}
