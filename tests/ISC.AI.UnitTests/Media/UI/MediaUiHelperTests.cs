using Xunit;
using System.Linq;
using ISC.AI.Modules.Media.UI;
using Shouldly;

namespace ISC.AI.UnitTests.Media.UI;

public sealed class MediaUiHelperTests
{
    [Fact(DisplayName = "Выделение найденного: все вхождения без учёта регистра, исходный текст сохраняется")]
    public void Highlight_splits_all_matches_case_insensitive_and_preserves_text()
    {
        const string text = "ал үйдө болчу жана ҮЙГӨ барды";
        var parts = TranscriptHighlight.Split(text, "  үй ");
        string.Concat(parts.Select(p => p.Text)).ShouldBe(text);
        parts.Count(p => p.IsMatch).ShouldBe(2);
        parts.Where(p => p.IsMatch).Select(p => p.Text).ShouldBe(["үй", "ҮЙ"]);
    }

    [Fact(DisplayName = "Без запроса или без совпадения — текст одним куском")]
    public void Highlight_no_query_or_no_match_single_part()
    {
        TranscriptHighlight.Split("привет мир", "").ShouldHaveSingleItem().IsMatch.ShouldBeFalse();
        TranscriptHighlight.Split("привет мир", "xyz").ShouldHaveSingleItem().Text.ShouldBe("привет мир");
        TranscriptHighlight.Split("", "мир").ShouldBeEmpty();
        TranscriptHighlight.Split(null, "мир").ShouldBeEmpty();
    }

    [Fact(DisplayName = "Выделение: вхождения не перекрываются, края строки обрабатываются")]
    public void Highlight_non_overlapping_and_edges()
    {
        var parts = TranscriptHighlight.Split("ааа", "аа");
        parts.Select(p => (p.Text, p.IsMatch)).ShouldBe([("аа", true), ("а", false)]);
        var whole = TranscriptHighlight.Split("Мир", "мир");
        whole.ShouldHaveSingleItem().IsMatch.ShouldBeTrue();
    }

    [Fact(DisplayName = "Таймкод: «мм:сс», от часа — «ч:мм:сс»")]
    public void ClockTimecode_formats()
    {
        MediaLabels.ClockTimecode(0).ShouldBe("00:00");
        MediaLabels.ClockTimecode(65_900).ShouldBe("01:05");
        MediaLabels.ClockTimecode(3_661_000).ShouldBe("1:01:01");
        MediaLabels.ClockTimecode(-5).ShouldBe("00:00");
        MediaLabels.ClockTimecode(36_000_000 + 5_000).ShouldBe("10:00:05");
    }

    [Fact(DisplayName = "Точный таймкод покадрового просмотра: «чч:мм:сс.ммм», отрицательное — начало записи")]
    public void PreciseTimecode_formats()
    {
        MediaLabels.PreciseTimecode(0).ShouldBe("00:00:00.000");
        MediaLabels.PreciseTimecode(40).ShouldBe("00:00:00.040");
        MediaLabels.PreciseTimecode(65_900).ShouldBe("00:01:05.900");
        MediaLabels.PreciseTimecode(3_661_001).ShouldBe("01:01:01.001");
        MediaLabels.PreciseTimecode(36_000_000 + 5_000).ShouldBe("10:00:05.000");
        MediaLabels.PreciseTimecode(-5).ShouldBe("00:00:00.000");
    }

    [Fact(DisplayName = "Подпись «кадр № N при F к/с» — по нативной частоте; без частоты подписи нет")]
    public void FrameLabel_by_native_frame_rate()
    {
        MediaLabels.FrameLabel(0, 25).ShouldBe("кадр № 0 при 25 к/с");
        MediaLabels.FrameLabel(40, 25).ShouldBe("кадр № 1 при 25 к/с");
        MediaLabels.FrameLabel(2_000, 25).ShouldBe("кадр № 50 при 25 к/с");
        MediaLabels.FrameLabel(1_000, 29.97).ShouldBe("кадр № 30 при 29.97 к/с");
        MediaLabels.FrameLabel(-40, 25).ShouldBe("кадр № 0 при 25 к/с");
        MediaLabels.FrameLabel(40, null).ShouldBeNull();
        MediaLabels.FrameLabel(40, 0).ShouldBeNull();
        MediaLabels.FrameLabel(40, double.NaN).ShouldBeNull();
    }

    [Fact(DisplayName = "Числовые параметры адреса: разбираются только десятичные цифры, остальное — «параметра нет»")]
    public void QueryValues_parse_digits_only()
    {
        MediaQueryValues.ParseInt("42").ShouldBe(42);
        MediaQueryValues.ParseInt("0").ShouldBe(0);
        MediaQueryValues.ParseInt("2147483647").ShouldBe(int.MaxValue);
        MediaQueryValues.ParseLong("65000").ShouldBe(65_000L);
        MediaQueryValues.ParseLong("99999999999").ShouldBe(99_999_999_999L);

        string?[] bad = [null, "", " ", "abc", "-5", "+5", " 5", "5 ", "1e5", "1.5", "1,5", "0x10", "2147483648", "99999999999999999999"];
        foreach (var value in bad)
        {
            MediaQueryValues.ParseInt(value).ShouldBeNull($"int: «{value}»");
        }

        foreach (var value in bad.Where(v => v != "2147483648"))
        {
            MediaQueryValues.ParseLong(value).ShouldBeNull($"long: «{value}»");
        }
    }

    [Fact(DisplayName = "Название формата для подсказки берётся из типа носителя")]
    public void FormatName_from_content_type()
    {
        MediaLabels.FormatName("video/x-matroska").ShouldBe("MKV");
        MediaLabels.FormatName("video/quicktime").ShouldBe("MOV");
        MediaLabels.FormatName("video/x-msvideo").ShouldBe("AVI");
        MediaLabels.FormatName("VIDEO/3GPP").ShouldBe("3GP");
        MediaLabels.FormatName("video/x-flv").ShouldBe("FLV");
        MediaLabels.FormatName("video/webm").ShouldBe("WEBM");
        MediaLabels.FormatName("").ShouldBe("неизвестного формата");
        MediaLabels.FormatName(null).ShouldBe("неизвестного формата");
    }

    [Fact(DisplayName = "Тип файла при загрузке: пустой или чужой тип браузера заменяется по расширению")]
    public void Upload_type_resolution()
    {
        MediaUploadTypes.Resolve("voice.amr", "").ShouldBe("audio/amr");
        MediaUploadTypes.Resolve("voice.AMR", null).ShouldBe("audio/amr");
        // 3GP: тип от браузера из allowlist сохраняется; без типа — видео, как у Chrome/Edge (вид не зависит
        // от браузера; голосовой 3GP без картинки переводит в аудио индексатор).
        MediaUploadTypes.Resolve("v.3gp", "video/3gpp").ShouldBe("video/3gpp");
        MediaUploadTypes.Resolve("v.3gp", "").ShouldBe("video/3gpp");
        MediaUploadTypes.Resolve("v.3GPP", null).ShouldBe("video/3gpp");
        MediaUploadTypes.Resolve("v.3gp", "audio/3gpp").ShouldBe("audio/3gpp");
        MediaUploadTypes.Resolve("v.opus", "").ShouldBe("audio/ogg");
        MediaUploadTypes.Resolve("a.m4a", "audio/x-m4a").ShouldBe("audio/x-m4a");
        MediaUploadTypes.Resolve("a.mp4", "video/mp4").ShouldBe("video/mp4");
        MediaUploadTypes.Resolve("a.exe", "application/x-msdownload").ShouldBe("application/x-msdownload");
        MediaUploadTypes.Resolve("a.html", "text/html").ShouldBe("text/html");
        MediaUploadTypes.Resolve("noext", "").ShouldBe("");
    }

    [Fact(DisplayName = "Выбор файлов предлагает аудиоформаты")]
    public void Accept_contains_audio()
    {
        var accept = MediaUploadTypes.Accept.Split(',');
        accept.ShouldContain(".jpg");
        accept.ShouldContain(".mkv");
        accept.ShouldContain(".amr");
        accept.ShouldContain(".3gp");
        accept.ShouldContain("audio/amr");
        accept.ShouldContain("audio/x-m4a");
        accept.Distinct(StringComparer.OrdinalIgnoreCase).Count().ShouldBe(accept.Length);
        MediaUploadTypes.AudioExtensionsHint.ShouldNotContain("3gp");
        System.Console.WriteLine(MediaUploadTypes.Accept);
        System.Console.WriteLine(MediaUploadTypes.AudioExtensionsHint);
    }
}
