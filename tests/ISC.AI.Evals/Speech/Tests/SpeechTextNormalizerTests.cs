using ISC.AI.Evals.Speech;
using Shouldly;
using Xunit;

namespace ISC.AI.Evals.Speech.Tests;

/// <summary>Нормализация перед WER/CER (КИ-10): одинаковая для эталона и модели, киргизские буквы не трогает.</summary>
public sealed class SpeechTextNormalizerTests
{
    [Fact(DisplayName = "Нормализация: нижний регистр, без знаков препинания, пробелы схлопнуты")]
    public void Lowercases_strips_punctuation_and_collapses_spaces()
    {
        SpeechTextNormalizer.Normalize("  Мама, мыла РАМУ!!! — «вчера»…  (да?) ")
            .ShouldBe("мама мыла раму вчера да");
    }

    [Fact(DisplayName = "Нормализация: табуляции, переводы строк и неразрывный пробел — один пробел")]
    public void Treats_any_whitespace_as_single_separator()
    {
        SpeechTextNormalizer.Normalize("а\t\tб\r\n  в г").ShouldBe("а б в г");
    }

    [Fact(DisplayName = "Нормализация: ё → е, в том числе ё, набранная как е + диереза")]
    public void Replaces_yo_with_ye()
    {
        SpeechTextNormalizer.Normalize("Ёлка ещё").ShouldBe("елка еще");
        SpeechTextNormalizer.Normalize("ёж").ShouldBe("еж");
    }

    [Fact(DisplayName = "Нормализация: киргизские ң ө ү сохраняются (заглавные → строчные), не заменяются на н о у")]
    public void Keeps_kyrgyz_letters_distinct()
    {
        var normalized = SpeechTextNormalizer.Normalize("ҮЙДӨ Өмүр, Ңаамат!");

        normalized.ShouldBe("үйдө өмүр ңаамат");
        normalized.ShouldNotContain('у');
        normalized.ShouldNotContain('о');
        normalized.ShouldNotContain('н');

        // «үй» (дом) и «уй» (корова) — разные слова и после нормализации.
        SpeechTextNormalizer.Normalize("Үй").ShouldNotBe(SpeechTextNormalizer.Normalize("Уй"));
    }

    [Fact(DisplayName = "Нормализация: дефис — граница слова (что-то, үй-бүлө)")]
    public void Hyphen_splits_words()
    {
        SpeechTextNormalizer.Words("Что-то про үй-бүлө").ShouldBe(["что", "то", "про", "үй", "бүлө"]);
    }

    [Fact(DisplayName = "Нормализация: числа не трогает — цифры остаются цифрами, слова словами")]
    public void Keeps_numbers_as_written()
    {
        SpeechTextNormalizer.Normalize("В 2024 году — 5,5%").ShouldBe("в 2024 году 5 5");
        SpeechTextNormalizer.Normalize("двадцать пятое").ShouldBe("двадцать пятое");
    }

    [Fact(DisplayName = "Нормализация: знак ударения и мягкий перенос удаляются без разрыва слова")]
    public void Removes_stress_marks_and_invisible_characters_inside_words()
    {
        SpeechTextNormalizer.Normalize("замо́к").ShouldBe("замок");
        SpeechTextNormalizer.Normalize("рас­шифровка").ShouldBe("расшифровка");
        SpeechTextNormalizer.Normalize("﻿начало").ShouldBe("начало");
    }

    [Fact(DisplayName = "Нормализация: пустой, пробельный и null текст — пустая строка и ноль слов")]
    public void Empty_input_gives_empty_output()
    {
        SpeechTextNormalizer.Normalize(null).ShouldBe(string.Empty);
        SpeechTextNormalizer.Normalize("   \r\n ").ShouldBe(string.Empty);
        SpeechTextNormalizer.Normalize("?!…").ShouldBe(string.Empty);
        SpeechTextNormalizer.Words(" , ").ShouldBeEmpty();
        SpeechTextNormalizer.Characters(null).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Нормализация: символы для CER — кодовые точки вместе с пробелами между словами")]
    public void Characters_include_single_spaces()
    {
        SpeechTextNormalizer.Characters("Үй, бар!").Length.ShouldBe("үй бар".Length);
    }

    [Fact(DisplayName = "Эталон: пометка [неразборчиво] сохраняется отдельным словом, остальное нормализуется как обычно")]
    public void Reference_keeps_unintelligible_marker()
    {
        SpeechTextNormalizer.NormalizeReference("Мама, [НЕРАЗБОРЧИВО]… Раму[ неразборчиво ]!")
            .ShouldBe("мама [неразборчиво] раму [неразборчиво]");
        SpeechTextNormalizer.ReferenceWords("[неразборчиво] бала").ShouldBe(["[неразборчиво]", "бала"]);
        SpeechTextNormalizer.CountUnintelligible("а [неразборчиво] б [Неразборчиво]").ShouldBe(2);
        SpeechTextNormalizer.CountScoredReferenceWords("а [неразборчиво] б").ShouldBe(2);

        // Без пометок эталон нормализуется так же, как выход модели.
        SpeechTextNormalizer.NormalizeReference("Ысык-Көл, ЁЛКА").ShouldBe(SpeechTextNormalizer.Normalize("Ысык-Көл, ЁЛКА"));
    }

    [Fact(DisplayName = "Эталон для CER: на месте пометки — джокер, пробелы вокруг него не пишутся")]
    public void Reference_characters_replace_marker_with_wildcard()
    {
        var units = SpeechTextNormalizer.ReferenceCharacters("аб [неразборчиво] в г");

        units.ShouldBe(new int[] { 'а', 'б', ErrorRateCalculator.Wildcard, 'в', ' ', 'г' });
    }

    [Fact(DisplayName = "Выход модели: скобки — границы слов, «[неразборчиво]» становится обычным словом")]
    public void Hypothesis_does_not_know_the_marker()
    {
        SpeechTextNormalizer.Normalize("да [неразборчиво] нет").ShouldBe("да неразборчиво нет");
    }
}
