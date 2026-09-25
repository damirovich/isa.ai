using System;
using System.Collections.Generic;
using System.Linq;
using ISC.AI.Evals.Speech;
using Shouldly;
using Xunit;

namespace ISC.AI.Evals.Speech.Tests;

/// <summary>WER/CER (КИ-10): расстояние Левенштейна, разбиение на замены/удаления/вставки, пустой эталон.</summary>
public sealed class ErrorRateCalculatorTests
{
    [Fact(DisplayName = "WER, классический пример: одна замена из шести слов — 1/6")]
    public void Classic_wer_example()
    {
        var counts = ErrorRateCalculator.WordErrors("the cat sat on the mat", "the cat sit on the mat");

        counts.ReferenceLength.ShouldBe(6);
        counts.Substitutions.ShouldBe(1);
        counts.Deletions.ShouldBe(0);
        counts.Insertions.ShouldBe(0);
        counts.Hits.ShouldBe(5);
        counts.Rate!.Value.ShouldBe(1.0 / 6, 1e-12);
    }

    [Fact(DisplayName = "WER: замена, пропуск и вставка считаются раздельно — 3 ошибки на 5 слов")]
    public void Counts_substitutions_deletions_and_insertions()
    {
        // «ну» — вставка, «раму» → «рамку» — замена, «вчера» — пропуск.
        var counts = ErrorRateCalculator.WordErrors("мама мыла раму вчера вечером", "ну мама мыла рамку вечером");

        counts.Substitutions.ShouldBe(1);
        counts.Deletions.ShouldBe(1);
        counts.Insertions.ShouldBe(1);
        counts.Errors.ShouldBe(3);
        counts.Rate!.Value.ShouldBe(0.6, 1e-12);
    }

    [Fact(DisplayName = "WER может быть больше 1: одно слово эталона, три у модели")]
    public void Wer_can_exceed_one()
    {
        var counts = ErrorRateCalculator.WordErrors("да", "нет не знаю");

        counts.Errors.ShouldBe(3);
        counts.Rate!.Value.ShouldBe(3.0, 1e-12);
    }

    [Fact(DisplayName = "CER, классический пример kitten → sitting: 2 замены и 1 вставка на 6 символов")]
    public void Classic_cer_example()
    {
        var counts = ErrorRateCalculator.CharacterErrors("kitten", "sitting");

        counts.Substitutions.ShouldBe(2);
        counts.Insertions.ShouldBe(1);
        counts.Deletions.ShouldBe(0);
        counts.Rate!.Value.ShouldBe(0.5, 1e-12);
    }

    [Fact(DisplayName = "Киргизские буквы: «уйдо» вместо «үйдө» — ошибка слова и две ошибки символов")]
    public void Kyrgyz_letters_are_not_equal_to_russian_lookalikes()
    {
        const string reference = "үйдө бала жок";
        const string hypothesis = "уйдо бала жок";

        var words = ErrorRateCalculator.WordErrors(reference, hypothesis);
        words.Substitutions.ShouldBe(1);
        words.Rate!.Value.ShouldBe(1.0 / 3, 1e-12);

        var characters = ErrorRateCalculator.CharacterErrors(reference, hypothesis);
        characters.ReferenceLength.ShouldBe(13); // 4 + пробел + 4 + пробел + 3
        characters.Substitutions.ShouldBe(2); // ү → у, ө → о
        characters.Rate!.Value.ShouldBe(2.0 / 13, 1e-12);
    }

    [Fact(DisplayName = "Регистр, знаки препинания и ё не считаются ошибками модели")]
    public void Formatting_differences_are_not_errors()
    {
        ErrorRateCalculator.WordErrors("Үйдө, бала ЖОК! Всё.", "үйдө бала жок все").Errors.ShouldBe(0);
        ErrorRateCalculator.CharacterErrors("Үйдө, бала ЖОК! Всё.", "үйдө бала жок все").Errors.ShouldBe(0);
    }

    [Fact(DisplayName = "Пустой эталон: доля не определена (делить не на что), слова модели видны как вставки")]
    public void Empty_reference()
    {
        var bothEmpty = ErrorRateCalculator.WordErrors("", "  ");
        bothEmpty.Rate.ShouldBeNull();
        bothEmpty.Errors.ShouldBe(0);

        var hallucination = ErrorRateCalculator.WordErrors("", "шум шум");
        hallucination.Rate.ShouldBeNull();
        hallucination.Insertions.ShouldBe(2);
        hallucination.ReferenceLength.ShouldBe(0);
    }

    [Fact(DisplayName = "Пустая гипотеза: все слова эталона — пропуски, WER = 1")]
    public void Empty_hypothesis_is_all_deletions()
    {
        var counts = ErrorRateCalculator.WordErrors("бир эки үч", null);

        counts.Deletions.ShouldBe(3);
        counts.Rate.ShouldBe(1.0);
    }

    [Fact(DisplayName = "Сумма счётчиков: WER группы = Σ ошибок / Σ слов эталона (взвешено по словам)")]
    public void Sum_gives_corpus_level_rate()
    {
        var longRecord = new EditCounts(ReferenceLength: 10, HypothesisLength: 10, Substitutions: 1, Deletions: 0, Insertions: 0);
        var shortRecord = new EditCounts(ReferenceLength: 2, HypothesisLength: 2, Substitutions: 2, Deletions: 0, Insertions: 0);

        var total = EditCounts.Sum([longRecord, shortRecord]);

        // Не среднее долей (10 % и 100 % → 55 %), а 3 / 12 = 25 %.
        total.Rate!.Value.ShouldBe(0.25, 1e-12);
    }

    [Fact(DisplayName = "Левенштейн: совпадает с полной таблицей на случайных последовательностях, тождества S/D/I выполняются")]
    public void Matches_reference_implementation_on_random_sequences()
    {
        var random = new Random(20260925);
        for (var round = 0; round < 500; round++)
        {
            var reference = RandomSequence(random, random.Next(0, 25));
            var hypothesis = RandomSequence(random, random.Next(0, 25));

            var counts = ErrorRateCalculator.CountEdits(reference, hypothesis);

            counts.Errors.ShouldBe(NaiveDistance(reference, hypothesis));
            (counts.Hits + counts.Substitutions + counts.Deletions).ShouldBe(reference.Length);
            (counts.Hits + counts.Substitutions + counts.Insertions).ShouldBe(hypothesis.Length);
            counts.Hits.ShouldBeGreaterThanOrEqualTo(0);
        }
    }

    [Fact(DisplayName = "Левенштейн: длинная последовательность — ошибки у начала, в середине и в конце найдены верно")]
    public void Handles_long_sequences()
    {
        var reference = Enumerable.Range(0, 5_000).Select(index => index % 97).ToArray();
        var hypothesis = reference.ToList();
        hypothesis[3] = -1; // замена у начала
        hypothesis.RemoveAt(2_500); // пропуск в середине
        hypothesis.Add(-2); // вставка в конце

        var counts = ErrorRateCalculator.CountEdits(reference, [.. hypothesis]);

        counts.Substitutions.ShouldBe(1);
        counts.Deletions.ShouldBe(1);
        counts.Insertions.ShouldBe(1);
    }

    // ----- Участки [неразборчиво] (методика пилота, 4.6): джокер в эталоне -----

    [Fact(DisplayName = "[неразборчиво] в середине: слова модели на его месте не ошибки, пометка не в знаменателе WER")]
    public void Unintelligible_in_the_middle_absorbs_hypothesis_words()
    {
        const string reference = "мама [неразборчиво] раму";

        var words = ErrorRateCalculator.WordErrors(reference, "мама мыла раму");
        words.ReferenceLength.ShouldBe(2);
        words.Errors.ShouldBe(0);
        words.Unscored.ShouldBe(1);
        words.Rate.ShouldBe(0.0);

        // Модель на этом месте ничего не выдала — тоже не ошибка.
        var nothing = ErrorRateCalculator.WordErrors(reference, "мама раму");
        nothing.Errors.ShouldBe(0);
        nothing.Unscored.ShouldBe(0);

        // Ошибки вне участка по-прежнему считаются: одна замена на два оцениваемых слова.
        var outside = ErrorRateCalculator.WordErrors(reference, "папа мыла много раму");
        outside.Substitutions.ShouldBe(1);
        outside.Insertions.ShouldBe(0);
        outside.Unscored.ShouldBe(2);
        outside.Rate!.Value.ShouldBe(0.5, 1e-12);
    }

    [Fact(DisplayName = "[неразборчиво] в начале эталона: лишние слова модели в начале поглощаются")]
    public void Unintelligible_at_the_start()
    {
        var words = ErrorRateCalculator.WordErrors("[неразборчиво] бала жок", "ну вот бала жок");
        words.ReferenceLength.ShouldBe(2);
        words.Errors.ShouldBe(0);
        words.Unscored.ShouldBe(2);

        ErrorRateCalculator.WordErrors("[неразборчиво] бала жок", "бала жок").Errors.ShouldBe(0);
        ErrorRateCalculator.WordErrors("[неразборчиво] бала жок", "бала").Deletions.ShouldBe(1);
    }

    [Fact(DisplayName = "[неразборчиво] в конце эталона: хвост гипотезы поглощается, пропуск до пометки — ошибка")]
    public void Unintelligible_at_the_end()
    {
        var words = ErrorRateCalculator.WordErrors("бала жок [неразборчиво]", "бала жок экен го");
        words.Errors.ShouldBe(0);
        words.Unscored.ShouldBe(2);

        var missing = ErrorRateCalculator.WordErrors("бала жок [неразборчиво]", "бала экен");
        missing.ReferenceLength.ShouldBe(2);
        missing.Errors.ShouldBe(1); // «жок» пропущено или заменено — одна ошибка, «экен» поглощён либо заменил «жок»
    }

    [Fact(DisplayName = "[неразборчиво] два подряд и два через слово: слово между участками оценивается")]
    public void Two_unintelligible_regions()
    {
        var adjacent = ErrorRateCalculator.WordErrors("бир [неразборчиво] [неразборчиво] үч", "бир эки беш үч");
        adjacent.ReferenceLength.ShouldBe(2);
        adjacent.Errors.ShouldBe(0);
        adjacent.Unscored.ShouldBe(2);

        const string separated = "бир [неразборчиво] эки [неразборчиво] төрт";
        var both = ErrorRateCalculator.WordErrors(separated, "бир x эки y z төрт");
        both.ReferenceLength.ShouldBe(3);
        both.Errors.ShouldBe(0);
        both.Unscored.ShouldBe(3);

        // «эки» между участками модель не выдала — это пропуск: участки не «съедают» оцениваемое слово.
        var lost = ErrorRateCalculator.WordErrors(separated, "бир x y төрт");
        lost.Errors.ShouldBe(1);
        lost.Rate!.Value.ShouldBe(1.0 / 3, 1e-12);
    }

    [Fact(DisplayName = "Эталон целиком из [неразборчиво]: оценивать нечего — ошибок нет, доля не определена")]
    public void Reference_made_only_of_unintelligible()
    {
        var words = ErrorRateCalculator.WordErrors("[неразборчиво]", "что угодно здесь");
        words.ReferenceLength.ShouldBe(0);
        words.Errors.ShouldBe(0);
        words.Unscored.ShouldBe(3);
        words.Rate.ShouldBeNull();

        var characters = ErrorRateCalculator.CharacterErrors("[неразборчиво]", "что угодно");
        characters.ReferenceLength.ShouldBe(0);
        characters.Errors.ShouldBe(0);
        characters.Unscored.ShouldBe("что угодно".Length);

        ErrorRateCalculator.WordErrors("[неразборчиво] [неразборчиво]", null).Errors.ShouldBe(0);
    }

    [Fact(DisplayName = "CER и [неразборчиво]: поглощаются и слова, и пробелы вокруг участка; в начале, середине и конце")]
    public void Unintelligible_in_character_error_rate()
    {
        var middle = ErrorRateCalculator.CharacterErrors("мама [неразборчиво] раму", "мама мыла раму");
        middle.ReferenceLength.ShouldBe(8); // «мама» + «раму»: пробелы вокруг участка не оцениваются
        middle.Errors.ShouldBe(0);
        middle.Unscored.ShouldBe(" мыла ".Length);

        // Без лишнего пробела: модель выдала «мама раму» — ни одной символьной ошибки.
        ErrorRateCalculator.CharacterErrors("мама [неразборчиво] раму", "мама раму").Errors.ShouldBe(0);

        ErrorRateCalculator.CharacterErrors("[неразборчиво] бала", "ну бала").Errors.ShouldBe(0);
        ErrorRateCalculator.CharacterErrors("бала [неразборчиво]", "бала го").Errors.ShouldBe(0);
        ErrorRateCalculator.CharacterErrors("бир [неразборчиво] [неразборчиво] үч", "бир эки үч").Errors.ShouldBe(0);

        // Ошибка в букве вне участка видна: «уч» вместо «үч».
        var outside = ErrorRateCalculator.CharacterErrors("бир [неразборчиво] үч", "бир эки уч");
        outside.Substitutions.ShouldBe(1);
        outside.Rate!.Value.ShouldBe(1.0 / 5, 1e-12); // «бир» + «үч» = 5 символов
    }

    [Fact(DisplayName = "CER и [неразборчиво]: участок поглощает только целые слова — буквы, приклеенные к соседнему слову, ошибки")]
    public void Unintelligible_in_character_error_rate_does_not_absorb_glued_letters()
    {
        // «үйгө» вместо «үй»: неверный аффикс — две лишние буквы, « бар» — целое слово на месте участка.
        // Дешевле не бывает: пропуск «үй» с поглощением всего выхода — тоже 2, но не меньше.
        var affix = ErrorRateCalculator.CharacterErrors("үй [неразборчиво]", "үйгө бар");
        affix.Errors.ShouldBe(2);
        affix.Insertions.ShouldBe(2);
        affix.Unscored.ShouldBe(" бар".Length);
        ErrorRateCalculator.WordErrors("үй [неразборчиво]", "үйгө бар").Errors.ShouldBe(1); // WER видит ту же ошибку

        // «о» приклеено к «бала» в начале — лишняя буква, а не неразборчивое слово.
        var glued = ErrorRateCalculator.CharacterErrors("[неразборчиво] бала", "обала");
        glued.Errors.ShouldBe(1);
        glued.Insertions.ShouldBe(1);
        glued.Unscored.ShouldBe(0);

        // Лишняя буква на стыке в середине — ошибка; склеенные соседние слова — нет: пробелы вокруг участка не в N.
        ErrorRateCalculator.CharacterErrors("мама [неразборчиво] раму", "мамаы раму").Insertions.ShouldBe(1);
        ErrorRateCalculator.CharacterErrors("мама [неразборчиво] раму", "мамаы раму").Errors.ShouldBe(1);
        ErrorRateCalculator.CharacterErrors("мама [неразборчиво] раму", "мамараму").Errors.ShouldBe(0);
    }

    [Fact(DisplayName = "Левенштейн с джокером по целым словам: совпадает с полной таблицей на случайных последовательностях, тождества выполняются")]
    public void Word_bounded_wildcard_matches_reference_implementation_on_random_sequences()
    {
        const int separator = 0; // «пробел»; буквы — 1..3
        var random = new Random(20260926);
        for (var round = 0; round < 3_000; round++)
        {
            var reference = Enumerable.Range(0, random.Next(0, 16))
                .Select(_ => random.Next(0, 6) == 0 ? ErrorRateCalculator.Wildcard : RandomUnit(random))
                .ToArray();

            // Половина гипотез — случайные, половина — эталон со словами на месте джокеров и парой правок:
            // так часты длинные общие начало и конец, упирающиеся в джокер, — проверяется их отсечение.
            var hypothesis = random.Next(0, 2) == 0
                ? Enumerable.Range(0, random.Next(0, 16)).Select(_ => RandomUnit(random)).ToArray()
                : Mutate(random, reference.SelectMany(unit => unit == ErrorRateCalculator.Wildcard
                    ? Enumerable.Range(0, random.Next(0, 5)).Select(_ => RandomUnit(random))
                    : Enumerable.Repeat(unit, 1)).ToList());

            var counts = ErrorRateCalculator.CountEdits(reference, hypothesis, separator);

            var context = $"эталон [{string.Join(",", reference)}], гипотеза [{string.Join(",", hypothesis)}]";
            counts.Errors.ShouldBe(NaiveDistance(reference, hypothesis, separator), context);
            counts.ReferenceLength.ShouldBe(reference.Count(unit => unit != ErrorRateCalculator.Wildcard));
            (counts.Hits + counts.Substitutions + counts.Deletions).ShouldBe(counts.ReferenceLength, context);
            (counts.Hits + counts.Substitutions + counts.Insertions + counts.Unscored).ShouldBe(hypothesis.Length, context);
            counts.Hits.ShouldBeGreaterThanOrEqualTo(0, context);
            counts.Unscored.ShouldBeGreaterThanOrEqualTo(0, context);
        }

        static int RandomUnit(Random random) => random.Next(0, 4) == 0 ? separator : random.Next(1, 4);

        static int[] Mutate(Random random, List<int> units)
        {
            for (var edit = random.Next(0, 3); edit > 0; edit--)
            {
                var position = random.Next(0, units.Count + 1);
                switch (random.Next(0, 3))
                {
                    case 0:
                        units.Insert(position, RandomUnit(random));
                        break;
                    case 1 when position < units.Count:
                        units.RemoveAt(position);
                        break;
                    case 2 when position < units.Count:
                        units[position] = RandomUnit(random);
                        break;
                }
            }

            return [.. units];
        }
    }

    [Fact(DisplayName = "[неразборчиво] распознаётся без учёта регистра и пробелов в скобках; в выходе модели — обычное слово")]
    public void Unintelligible_marker_variants()
    {
        ErrorRateCalculator.WordErrors("да [ НЕРАЗБОРЧИВО ] нет", "да что-то нет").Errors.ShouldBe(0);
        ErrorRateCalculator.WordErrors("да, [Неразборчиво]. Нет!", "да нет").Errors.ShouldBe(0);

        // Пометку ставит только человек в эталоне: у модели «[неразборчиво]» — просто слово «неразборчиво».
        ErrorRateCalculator.WordErrors("да нет", "да [неразборчиво] нет").Insertions.ShouldBe(1);
    }

    [Fact(DisplayName = "Левенштейн с джокером: совпадает с полной таблицей на случайных последовательностях, тождества выполняются")]
    public void Wildcard_matches_reference_implementation_on_random_sequences()
    {
        var random = new Random(20260925);
        for (var round = 0; round < 2_000; round++)
        {
            // Эталон: единицы 0..3 и джокеры (−1), гипотеза — только обычные единицы.
            var reference = Enumerable.Range(0, random.Next(0, 20))
                .Select(_ => random.Next(0, 6) == 0 ? ErrorRateCalculator.Wildcard : random.Next(0, 4))
                .ToArray();
            var hypothesis = RandomSequence(random, random.Next(0, 20));

            var counts = ErrorRateCalculator.CountEdits(reference, hypothesis);

            counts.Errors.ShouldBe(NaiveDistance(reference, hypothesis), $"эталон [{string.Join(",", reference)}], гипотеза [{string.Join(",", hypothesis)}]");
            counts.ReferenceLength.ShouldBe(reference.Count(unit => unit != ErrorRateCalculator.Wildcard));
            (counts.Hits + counts.Substitutions + counts.Deletions).ShouldBe(counts.ReferenceLength);
            (counts.Hits + counts.Substitutions + counts.Insertions + counts.Unscored).ShouldBe(hypothesis.Length);
            counts.Hits.ShouldBeGreaterThanOrEqualTo(0);
            counts.Unscored.ShouldBeGreaterThanOrEqualTo(0);
            if (!reference.Contains(ErrorRateCalculator.Wildcard))
            {
                counts.Unscored.ShouldBe(0);
            }
        }
    }

    private static int[] RandomSequence(Random random, int length) =>
        Enumerable.Range(0, length).Select(_ => random.Next(0, 4)).ToArray();

    // Эталонная реализация «в лоб» — полная таблица, прямо по определению: строка джокера — минимум
    // предыдущей строки по всем отрезкам гипотезы [k, j), которые участок может поглотить бесплатно. Без
    // разделителя — любые; с разделителем — пустой где угодно, непустой только от границы слова до границы.
    private static int NaiveDistance(int[] reference, int[] hypothesis, int? separator = null)
    {
        var table = new int[reference.Length + 1, hypothesis.Length + 1];
        for (var j = 0; j <= hypothesis.Length; j++)
        {
            table[0, j] = j;
        }

        for (var i = 1; i <= reference.Length; i++)
        {
            var wildcard = reference[i - 1] == ErrorRateCalculator.Wildcard;
            table[i, 0] = table[i - 1, 0] + (wildcard ? 0 : 1);
            for (var j = 1; j <= hypothesis.Length; j++)
            {
                if (wildcard)
                {
                    var best = int.MaxValue;
                    for (var k = 0; k <= j; k++)
                    {
                        if (k == j || (IsBoundary(hypothesis, k, separator) && IsBoundary(hypothesis, j, separator)))
                        {
                            best = Math.Min(best, table[i - 1, k]);
                        }
                    }

                    table[i, j] = best;
                    continue;
                }

                var substitution = table[i - 1, j - 1] + (reference[i - 1] == hypothesis[j - 1] ? 0 : 1);
                table[i, j] = Math.Min(substitution, Math.Min(table[i - 1, j] + 1, table[i, j - 1] + 1));
            }
        }

        return table[reference.Length, hypothesis.Length];
    }

    private static bool IsBoundary(int[] hypothesis, int position, int? separator) =>
        separator is null
        || position == 0
        || position == hypothesis.Length
        || hypothesis[position - 1] == separator
        || hypothesis[position] == separator;
}
