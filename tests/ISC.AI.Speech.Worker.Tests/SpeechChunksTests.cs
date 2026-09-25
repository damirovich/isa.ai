using System;
using System.Collections.Generic;
using System.Linq;
using Shouldly;
using Xunit;

namespace ISC.AI.Speech.Worker.Tests;

/// <summary>
/// Склейка соседних участков речи в кусок для модели и раскладка слов обратно по участкам (<see cref="SpeechChunks"/>,
/// ADR-0026): на коротком участке модель путает киргизский с казахским, поэтому участки подаются вместе, а таймкоды
/// фрагментов не должны от этого грубеть и текст не должен теряться или повторяться.
/// </summary>
public sealed class SpeechChunksTests
{
    private const int Rate = 16_000;
    private const int Max = 20 * Rate;

    // ---- Когда склеивать и когда подавать модели ----

    [Theory(DisplayName = "Склейка: участок присоединяется, пока весь кусок с паузами не длиннее предела модели")]
    [InlineData(0L, 320_000L, true)]
    [InlineData(0L, 320_001L, false)]
    [InlineData(100_000L, 420_000L, true)]
    [InlineData(100_000L, 420_001L, false)]
    public void Piece_fits_while_chunk_within_limit(long chunkStart, long pieceEnd, bool fits) =>
        SpeechChunks.Fits(chunkStart, pieceEnd, Max).ShouldBe(fits);

    [Fact(DisplayName = "Склейка подаётся модели, как только ни один будущий участок в предел уже не уложится")]
    public void Chunk_is_sealed_after_limit_plus_detector_delay()
    {
        const long start = 50_000;
        var limit = start + Max + SpeechChunks.SealMarginSamples;

        SpeechChunks.IsSealed(start, limit, Max).ShouldBeFalse();
        SpeechChunks.IsSealed(start, limit + 1, Max).ShouldBeTrue();

        // Запас покрывает задержку детектора: участок отдаётся не раньше паузы MinSilenceDuration за его концом.
        SpeechChunks.SealMarginSamples.ShouldBeGreaterThanOrEqualTo(VadSettings.MinSilenceSamples + VadSettings.WindowSize);
    }

    // ---- Раскладка слов по участкам ----

    [Fact(DisplayName = "Раскладка: буквенные токены GigaAM с пробелами — каждое слово попадает в участок своей первой буквы")]
    public void Character_tokens_are_split_into_pieces_by_first_letter_time()
    {
        // Кусок начинается с 1 с записи; участки: [1,0; 2,0) и [2,5; 4,0) с на шкале записи.
        var pieces = Pieces((1.0, 2.0), (2.5, 4.0));
        var (tokens, times) = Tokens(("бир", 0.10), ("аз", 0.60), ("кечигип", 1.55), ("калдым", 2.20));

        var texts = Texts(SpeechChunks.AssignWords(tokens, times, Samples(1.0), pieces, Rate));

        texts.ShouldBe(["бир аз", "кечигип калдым"]);
    }

    [Fact(DisplayName = "Раскладка: токены SentencePiece с «▁» в начале слова разбираются так же")]
    public void Sentencepiece_tokens_start_words_with_marker()
    {
        var pieces = Pieces((0.0, 1.0), (1.5, 3.0));
        string[] tokens = ["▁ко", "оп", "суз", "дук", "▁талап", "тарын"];
        float[] times = [0.10f, 0.14f, 0.18f, 0.22f, 1.60f, 1.70f];

        Texts(SpeechChunks.AssignWords(tokens, times, 0, pieces, Rate)).ShouldBe(["коопсуздук", "талаптарын"]);
    }

    [Theory(DisplayName = "Раскладка: слово в паузе между участками — к ближайшему, при равенстве — к следующему")]
    [InlineData(1.10, 0)] // ближе к концу первого (1,0)
    [InlineData(1.40, 1)] // ближе к началу второго (1,5)
    [InlineData(1.25, 1)] // посередине — к следующему
    public void Word_in_gap_goes_to_nearest_piece(double time, int expectedPiece)
    {
        var pieces = Pieces((0.0, 1.0), (1.5, 3.0));
        var (tokens, times) = Tokens(("сөз", time));

        var texts = Texts(SpeechChunks.AssignWords(tokens, times, 0, pieces, Rate))!;

        texts[expectedPiece].ShouldBe("сөз");
        texts[1 - expectedPiece].ShouldBeEmpty();
    }

    [Fact(DisplayName = "Раскладка: слово до первого участка — в первый, после последнего — в последний")]
    public void Words_outside_pieces_go_to_first_or_last()
    {
        var pieces = Pieces((1.0, 2.0), (3.0, 4.0));
        var (tokens, times) = Tokens(("алло", 0.0), ("жду", 4.5));

        Texts(SpeechChunks.AssignWords(tokens, times, Samples(0.5), pieces, Rate)).ShouldBe(["алло", "жду"]);
    }

    [Fact(DisplayName = "Раскладка: порядок слов сохраняется, ни одно слово не теряется и не повторяется")]
    public void Assignment_preserves_every_word_in_order()
    {
        var pieces = Pieces((0.0, 2.0), (2.4, 5.0), (5.6, 9.0), (9.3, 12.0));
        var words = Enumerable.Range(0, 40).Select(i => ("w" + i, i * 0.29)).ToArray();
        var (tokens, times) = Tokens(words);

        var texts = Texts(SpeechChunks.AssignWords(tokens, times, 0, pieces, Rate))!;

        string.Join(' ', texts.Where(t => t.Length > 0)).ShouldBe(string.Join(' ', words.Select(w => w.Item1)));
    }

    [Fact(DisplayName = "Раскладка: участок без слов — пустая строка (в протокол он не пишется)")]
    public void Piece_without_words_gets_empty_text()
    {
        var pieces = Pieces((0.0, 1.0), (2.0, 3.0), (4.0, 5.0));
        var (tokens, times) = Tokens(("бир", 0.2), ("үч", 4.2));

        Texts(SpeechChunks.AssignWords(tokens, times, 0, pieces, Rate)).ShouldBe(["бир", "", "үч"]);
    }

    [Fact(DisplayName = "Раскладка невозможна без меток или при несовпадении их числа — null (тогда один фрагмент на кусок)")]
    public void Missing_or_mismatched_timestamps_give_null()
    {
        var pieces = Pieces((0.0, 1.0));
        string[] tokens = ["а", "б"];

        SpeechChunks.AssignWords(tokens, null, 0, pieces, Rate).ShouldBeNull();
        SpeechChunks.AssignWords(null, [0.1f], 0, pieces, Rate).ShouldBeNull();
        SpeechChunks.AssignWords(tokens, [0.1f], 0, pieces, Rate).ShouldBeNull();
        SpeechChunks.AssignWords(tokens, [0.1f, 0.2f], 0, [], Rate).ShouldBeNull();
    }

    [Fact(DisplayName = "Сверка раскладки с текстом модели — без учёта лишних пробелов")]
    public void Normalize_spaces_collapses_whitespace()
    {
        SpeechChunks.NormalizeSpaces("  бир   аз\tкечигип ").ShouldBe("бир аз кечигип");
        SpeechChunks.NormalizeSpaces(null).ShouldBe(string.Empty);
    }

    // ---- Границы фрагментов: слово в паузе не уводит таймкод ----

    [Fact(DisplayName = "Границы: слово внутри участка — у фрагмента границы самого участка")]
    public void Word_inside_piece_keeps_piece_bounds()
    {
        var pieces = Pieces((1.0, 2.0), (3.0, 4.0));
        var (tokens, times) = Tokens(("бир", 0.2), ("эки", 2.2)); // от начала куска 1,0 с: 1,2 и 3,2 с записи

        var words = SpeechChunks.AssignWords(tokens, times, Samples(1.0), pieces, Rate)!;
        var bounds = SpeechChunks.FragmentBounds(pieces, words, Samples(1.0), Samples(4.0));

        bounds.ShouldBe([(Samples(1.0), Samples(2.0)), (Samples(3.0), Samples(4.0))]);
    }

    [Fact(DisplayName = "Границы: слово, найденное моделью в паузе перед участком, — фрагмент начинается с этого слова, а не после него")]
    public void Word_in_gap_before_piece_extends_fragment_start()
    {
        // Участки [0; 1) и [10; 12) с; «да» прозвучало на 7-й секунде — ближе ко второму участку.
        var pieces = Pieces((0.0, 1.0), (10.0, 12.0));
        var (tokens, times) = Tokens(("алло", 0.1), ("да", 7.0), ("жок", 10.5));

        var words = SpeechChunks.AssignWords(tokens, times, 0, pieces, Rate)!;
        var bounds = SpeechChunks.FragmentBounds(pieces, words, 0, Samples(12.0));

        words[1].Text.ShouldBe("да жок");
        bounds[1].Start.ShouldBe(Samples(7.0)); // переход к фрагменту не перематывает запись ПОСЛЕ «да»
        bounds[1].End.ShouldBe(Samples(12.0));
        bounds[0].ShouldBe((0L, Samples(1.0)));
    }

    [Fact(DisplayName = "Границы: слово в паузе после участка — фрагмент тянется до конца слова")]
    public void Word_in_gap_after_piece_extends_fragment_end()
    {
        var pieces = Pieces((0.0, 1.0), (10.0, 12.0));
        var (tokens, times) = Tokens(("алло", 0.1), ("угу", 3.0), ("жок", 10.5));

        var words = SpeechChunks.AssignWords(tokens, times, 0, pieces, Rate)!;
        var bounds = SpeechChunks.FragmentBounds(pieces, words, 0, Samples(12.0));

        words[0].Text.ShouldBe("алло угу");
        // Конец — метка последней буквы «угу» (3,08 с) плюс шаг метки 40 мс.
        bounds[0].End.ShouldBe(Samples(3.08) + SpeechChunks.TokenStepSamples);
    }

    [Fact(DisplayName = "Границы: расширенные фрагменты по порядку, не перекрываются и не выходят за склейку")]
    public void Extended_bounds_are_ordered_non_overlapping_and_inside_chunk()
    {
        var pieces = Pieces((0.5, 1.0), (1.2, 2.0), (2.1, 3.0));
        // Слова вплотную к стыкам и за краями склейки.
        var (tokens, times) = Tokens(("а", 0.0), ("бб", 1.08), ("вв", 1.12), ("г", 2.05), ("дд", 3.2));

        var words = SpeechChunks.AssignWords(tokens, times, 0, pieces, Rate)!;
        var bounds = SpeechChunks.FragmentBounds(pieces, words, Samples(0.5), Samples(3.0));

        var written = bounds.Where((_, i) => !words[i].IsEmpty).ToList();
        for (var i = 0; i < written.Count; i++)
        {
            written[i].Start.ShouldBeGreaterThanOrEqualTo(Samples(0.5));
            written[i].End.ShouldBeLessThanOrEqualTo(Samples(3.0));
            written[i].End.ShouldBeGreaterThanOrEqualTo(written[i].Start);
            if (i > 0)
            {
                written[i].Start.ShouldBeGreaterThanOrEqualTo(written[i - 1].End);
            }
        }
    }

    private static string[]? Texts(PieceWords[]? words) => words?.Select(w => w.Text).ToArray();

    private static long Samples(double seconds) => (long)Math.Round(seconds * Rate);

    private static List<(long Start, long End)> Pieces(params (double Start, double End)[] seconds) =>
        seconds.Select(s => (Samples(s.Start), Samples(s.End))).ToList();

    // Буквенные токены, как у GigaAM: каждая буква — токен с меткой (шаг 40 мс), между словами — токен-пробел.
    // Время слова задаётся относительно начала куска.
    private static (string[] Tokens, float[] Times) Tokens(params (string Word, double Start)[] words)
    {
        var tokens = new List<string>();
        var times = new List<float>();
        foreach (var (word, start) in words)
        {
            if (tokens.Count > 0)
            {
                tokens.Add(" ");
                times.Add((float)(start - 0.04));
            }

            for (var i = 0; i < word.Length; i++)
            {
                tokens.Add(word[i].ToString());
                times.Add((float)(start + i * 0.04));
            }
        }

        return ([.. tokens], [.. times]);
    }
}
