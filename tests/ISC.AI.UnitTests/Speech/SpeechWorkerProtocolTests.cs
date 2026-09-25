using System;
using System.Collections.Generic;
using System.Linq;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Speech.Protocol;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Speech;

/// <summary>
/// Разбор протокола процесса-распознавателя (ADR-0026) без процесса: строки фрагментов и итога, пустой
/// текст, битые строки, целостность вывода (порядок номеров, итоговая строка, сверка числа фрагментов).
/// </summary>
public sealed class SpeechWorkerProtocolTests
{
    [Fact(DisplayName = "Протокол: строка фрагмента разбирается в номер, таймкоды и дословный текст")]
    public void Segment_line_is_parsed()
    {
        var message = SpeechWorkerProtocol.ParseLine(
            """{"type":"segment","index":0,"startMs":1230,"endMs":4560,"text":"үйгө бардым домой"}""");

        var segment = message.ShouldBeOfType<SpeechWorkerSegment>();
        segment.Index.ShouldBe(0);
        segment.StartMs.ShouldBe(1230);
        segment.EndMs.ShouldBe(4560);
        segment.Text.ShouldBe("үйгө бардым домой");
    }

    [Fact(DisplayName = "Протокол: экранированная кириллица (\\uXXXX) даёт те же буквы, включая киргизские ң, ө, ү")]
    public void Escaped_cyrillic_is_decoded()
    {
        var message = SpeechWorkerProtocol.ParseLine(
            """{"type":"segment","index":3,"startMs":0,"endMs":10,"text":"ңөү да"}""");

        message.ShouldBeOfType<SpeechWorkerSegment>().Text.ShouldBe("ңөү да");
    }

    [Fact(DisplayName = "Протокол: итоговая строка done — длительность записи и число фрагментов")]
    public void Done_line_is_parsed()
    {
        var done = SpeechWorkerProtocol.ParseLine("""{"type":"done","durationMs":123456,"segments":7}""")
            .ShouldBeOfType<SpeechWorkerDone>();

        done.DurationMs.ShouldBe(123456);
        done.Segments.ShouldBe(7);
    }

    [Theory(DisplayName = "Протокол: пустая строка и строка из пробелов не нарушают протокол")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_line_is_ignored(string? line) =>
        SpeechWorkerProtocol.ParseLine(line).ShouldBeNull();

    [Theory(DisplayName = "Протокол: битая строка — FormatException с причиной")]
    [InlineData("{\"type\":\"segment\",\"index\":0", "не является JSON")]
    [InlineData("[1,2,3]", "ожидался JSON-объект")]
    [InlineData("{\"index\":0}", "type")]
    [InlineData("{\"type\":\"progress\",\"percent\":5}", "неизвестный тип")]
    [InlineData("{\"type\":\"segment\",\"index\":0,\"startMs\":10,\"text\":\"а\"}", "endMs")]
    [InlineData("{\"type\":\"segment\",\"index\":0,\"startMs\":\"10\",\"endMs\":20,\"text\":\"а\"}", "startMs")]
    [InlineData("{\"type\":\"segment\",\"index\":0,\"startMs\":50,\"endMs\":20,\"text\":\"а\"}", "неверными номером или границами")]
    [InlineData("{\"type\":\"segment\",\"index\":-1,\"startMs\":0,\"endMs\":20,\"text\":\"а\"}", "неверными номером или границами")]
    [InlineData("{\"type\":\"done\",\"durationMs\":-5,\"segments\":0}", "отрицательными")]
    public void Broken_line_is_explicit_error(string line, string expected)
    {
        var error = Should.Throw<FormatException>(() => SpeechWorkerProtocol.ParseLine(line));
        error.Message.ShouldContain(expected);
    }

    [Fact(DisplayName = "ТД-007: текст битой строки (возможно, расшифровка материала дела) в сообщение об ошибке не попадает")]
    public void Broken_line_content_is_not_echoed()
    {
        const string secret = "секретная фраза из допроса";
        var error = Should.Throw<FormatException>(() =>
            SpeechWorkerProtocol.ParseLine("{\"type\":\"segment\",\"index\":0,\"text\":\"" + secret + "\""));

        error.Message.ShouldNotContain(secret);
        error.Message.ShouldNotContain("допроса");
    }

    [Fact(DisplayName = "Вывод: фрагменты отдаются по порядку, пустой текст пропускается, номера отданных — подряд с нуля")]
    public void Output_skips_empty_text_and_renumbers()
    {
        var parser = new SpeechWorkerOutputParser();
        var lines = new[]
        {
            """{"type":"segment","index":0,"startMs":100,"endMs":900,"text":"  салам  "}""",
            """{"type":"segment","index":1,"startMs":1000,"endMs":1400,"text":"   "}""",
            "",
            """{"type":"segment","index":2,"startMs":1500,"endMs":3000,"text":"привет как дела"}""",
            """{"type":"done","durationMs":3500,"segments":3}""",
        };

        var accepted = lines.Select(parser.Accept).Where(s => s is not null).Cast<TranscriptSegmentDraft>().ToList();
        parser.EnsureCompleted();

        accepted.ShouldBe(new[]
        {
            new TranscriptSegmentDraft(0, 100, 900, "салам"),
            new TranscriptSegmentDraft(1, 1500, 3000, "привет как дела"),
        });
        parser.Done.ShouldBe(new SpeechWorkerDone(3500, 3));
    }

    [Fact(DisplayName = "Вывод: запись без речи — ни одного фрагмента и итог done с нулём")]
    public void Silence_is_zero_segments_and_done()
    {
        var parser = new SpeechWorkerOutputParser();
        parser.Accept("""{"type":"done","durationMs":5000,"segments":0}""").ShouldBeNull();

        Should.NotThrow(parser.EnsureCompleted);
        parser.Done!.Segments.ShouldBe(0);
    }

    [Fact(DisplayName = "Вывод: без итоговой строки done расшифровка считается оборванной (процесс упал посреди записи)")]
    public void Missing_done_means_truncated_output()
    {
        var parser = new SpeechWorkerOutputParser();
        parser.Accept("""{"type":"segment","index":0,"startMs":0,"endMs":500,"text":"бир"}""").ShouldNotBeNull();

        Should.Throw<FormatException>(parser.EnsureCompleted).Message.ShouldContain("оборван");
    }

    [Fact(DisplayName = "Вывод: потерянный или повторённый фрагмент (разрыв номеров) — ошибка протокола с номером строки")]
    public void Index_gap_is_error()
    {
        var parser = new SpeechWorkerOutputParser();
        parser.Accept("""{"type":"segment","index":0,"startMs":0,"endMs":500,"text":"бир"}""");

        var error = Should.Throw<FormatException>(() =>
            parser.Accept("""{"type":"segment","index":2,"startMs":600,"endMs":900,"text":"эки"}"""));
        error.Message.ShouldContain("строка 2");
        error.Message.ShouldContain("№ 1");
    }

    [Fact(DisplayName = "Вывод: итог сообщает иное число фрагментов, чем получено — ошибка")]
    public void Done_count_mismatch_is_error()
    {
        var parser = new SpeechWorkerOutputParser();
        parser.Accept("""{"type":"segment","index":0,"startMs":0,"endMs":500,"text":"бир"}""");

        Should.Throw<FormatException>(() => parser.Accept("""{"type":"done","durationMs":900,"segments":2}"""))
            .Message.ShouldContain("2 фрагм.");
    }

    [Fact(DisplayName = "Вывод: строка после итоговой done — нарушение протокола")]
    public void Output_after_done_is_error()
    {
        var parser = new SpeechWorkerOutputParser();
        parser.Accept("""{"type":"done","durationMs":0,"segments":0}""");

        Should.Throw<FormatException>(() =>
                parser.Accept("""{"type":"segment","index":0,"startMs":0,"endMs":1,"text":"а"}"""))
            .Message.ShouldContain("после итоговой");
    }

    [Fact(DisplayName = "Коды выхода: каждому штатному коду — своё объяснение; прочие — авария с кодом")]
    public void Exit_codes_are_described()
    {
        var described = new List<string>
        {
            SpeechWorkerExitCodes.Describe(SpeechWorkerExitCodes.InvalidArguments),
            SpeechWorkerExitCodes.Describe(SpeechWorkerExitCodes.ModelLoadFailed),
            SpeechWorkerExitCodes.Describe(SpeechWorkerExitCodes.RecognitionFailed),
        };

        described.Distinct().Count().ShouldBe(3);
        described[1].ShouldContain("модель");
        SpeechWorkerExitCodes.Describe(-1073741819).ShouldContain("аварийно");
    }
}
