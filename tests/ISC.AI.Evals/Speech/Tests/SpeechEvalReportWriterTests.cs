using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ISC.AI.Evals.Speech;
using ISC.AI.Modules.Media.Domain.Model;
using Shouldly;
using Xunit;

namespace ISC.AI.Evals.Speech.Tests;

/// <summary>Отчёт прогона: Markdown (сводки по языку и условиям, худшие записи), CSV и файлы расшифровок.</summary>
public sealed class SpeechEvalReportWriterTests
{
    private static readonly string DatasetFolder = Path.Combine(Path.GetTempPath(), "iscai-speech-eval-fake-set");

    [Fact(DisplayName = "Markdown: модель, методика, три языка (пустой — «нет записей»), худшие записи, сбои, замечания")]
    public void Markdown_contains_all_sections()
    {
        var markdown = SpeechEvalMarkdownWriter.ToMarkdown(SampleReport());

        markdown.ShouldContain("`fake-asr@0123456789ab`");
        markdown.ShouldContain("## По языку");
        markdown.ShouldContain("| Гипотезы | распознаватель системы");
        markdown.ShouldContain("| киргизский | 1 | 3 | 0 | 33,3 % | 1 | 0 | 0 | 0 | 15,4 % |");
        markdown.ShouldContain("## Контрольные файлы без речи");
        markdown.ShouldContain("ложных слов 1, ложных фрагментов 1; на 10 минут — фрагментов 1,0, слов 1,0");
        markdown.ShouldContain("| `hum.wav` | other | 10:00 | 1 | 1 | гул |");
        markdown.ShouldNotContain("| `hum.wav` | -"); // в таблицу речевых записей контрольный файл не попадает
        markdown.ShouldContain("| смешанная речь | 0 | — | | нет записей |");
        markdown.ShouldContain("## По условиям записи");
        markdown.ShouldContain("## Язык × условия");
        markdown.ShouldContain("## Худшие записи (по WER)");
        markdown.ShouldContain("## Сбои распознавания");
        markdown.ShouldContain("`broken.wav`: InvalidOperationException: сбой");
        markdown.ShouldContain("## Замечания к набору");
        markdown.ShouldContain("шум \\| ветер"); // вертикальная черта в примечании экранирована
    }

    [Fact(DisplayName = "CSV по записям: заголовок, строка на запись, поле с «;» в кавычках, доли с точкой")]
    public void Records_csv_is_machine_readable()
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        SpeechEvalCsvWriter.WriteRecords(SampleReport(), writer, CultureInfo.InvariantCulture);
        var lines = writer.ToString().TrimEnd().Split(Environment.NewLine);

        lines.Length.ShouldBe(5);
        lines[0].ShouldEndWith(";unintelligible;word_unscored;char_unscored;text_segments;hypothesis_source");
        lines[4].ShouldStartWith("fake-asr@0123456789ab;hum.wav;-;other;гул;ok;;1;0;1;;0;0;1;");
        lines[0].ShouldStartWith("model;file;language;condition;note;status;error;segments;ref_words;hyp_words;wer;");
        lines[1].ShouldStartWith("fake-asr@0123456789ab;ky.ogg;ky;phone;\"шум | ветер; рынок\";ok;;2;3;3;0.3333;1;0;0;13;13;0.1538;");
        lines[3].ShouldContain(";failed;InvalidOperationException: сбой;");
    }

    [Fact(DisplayName = "CSV сводок: язык, условия и их сочетания; культура с запятой для русского Excel")]
    public void Summary_csv_supports_russian_decimal_comma()
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        SpeechEvalCsvWriter.WriteSummary(SampleReport(), writer, CultureInfo.GetCultureInfo("ru-RU"));
        var text = writer.ToString();

        text.ShouldContain("fake-asr@0123456789ab;language;ky;1;0;3;0,3333;");
        text.ShouldContain(";language;mixed;0;0;0;;");
        text.ShouldContain(";condition;phone;");
        text.ShouldContain(";language_condition;ky/phone;");
        text.ShouldContain("fake-asr@0123456789ab;no_speech;-;1;0;;;;;;;;;;;600,000;;;;;1;1;");
        text.ShouldStartWith("model;group_by;group;records;failed;");
        text.ShouldContain(";false_words;false_fragments;unintelligible");
        text.ShouldNotContain(";condition;other;"); // контрольный файл не образует группу условий
    }

    [Fact(DisplayName = "Отчёт: запись «только скорость» — отдельный блок и строка speed_only; в сводках качества и «Все записи» её нет")]
    public void Speed_only_recordings_are_reported_separately()
    {
        var sample = SampleReport();
        var longRecording = Result(
            Recording("long.m4a", SpeechLanguage.Kyrgyz, RecordingCondition.Far, "[неразборчиво]", "2 ч"),
            [new TranscriptSegmentDraft(0, 0, 60_000, "көп сөз бар")],
            TimeSpan.FromMinutes(20),
            TimeSpan.FromHours(1));
        var report = sample with { Records = [.. sample.Records, longRecording] };

        var markdown = SpeechEvalMarkdownWriter.ToMarkdown(report);
        markdown.ShouldContain("| киргизский | 1 | 3 | 0 | 33,3 % | 1 | 0 | 0 | 0 | 15,4 % |"); // сводка по языку та же, что без неё
        markdown.ShouldNotContain("| дальний микрофон |"); // группы условий из одной записи «для скорости» нет
        markdown.ShouldNotContain("| `long.m4a` | ky | far | 0 |"); // в «Все записи» её нет
        markdown.ShouldContain("## Только скорость");
        markdown.ShouldContain("**Итого:** записей 1, звук 1:00:00, время 20:00, RTF 0,333.");
        markdown.ShouldContain("| `long.m4a` | ky | far | 1:00:00 | 20:00 | 0,333 | 3 | 1 | 2 ч |");
        markdown.ShouldContain("## Записи с большой долей неразборчивого");
        markdown.ShouldContain("Таких записей нет."); // у записи «для скорости» доля на 0 слов не определена

        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        SpeechEvalCsvWriter.WriteSummary(report, writer, CultureInfo.InvariantCulture);
        var summary = writer.ToString();
        summary.ShouldContain("fake-asr@0123456789ab;language;ky;1;0;3;0.3333;");
        summary.ShouldContain(";language;ky;1;0;3;0.3333;1;0;0;13;0.1538;2;0;0;30.000;3.000;0.1000;no;0;;;0" + Environment.NewLine); // пометка записи «для скорости» не в сводке
        summary.ShouldContain("fake-asr@0123456789ab;speed_only;all;1;0;;;;;;;;;;;3600.000;1200.000;0.3333;no;;;;" + Environment.NewLine);
        summary.ShouldNotContain(";condition;far;");
    }

    [Fact(DisplayName = "Экспорт: report.md, CSV, расшифровка с таймкодами и файлы «по слову в строке»; не внутрь набора")]
    public async Task Export_writes_files_outside_dataset()
    {
        var output = Path.Combine(Path.GetTempPath(), "iscai-speech-eval-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var report = SampleReport();

            var written = await SpeechEvalReportExporter.ExportAsync(report, output, csvNumberFormat: CultureInfo.InvariantCulture);

            written.ShouldContain(Path.Combine(output, "report.md"));
            written.ShouldContain(Path.Combine(output, "records.csv"));
            written.ShouldContain(Path.Combine(output, "summary.csv"));
            (await File.ReadAllBytesAsync(Path.Combine(output, "records.csv"))).Take(3).ShouldBe(new byte[] { 0xEF, 0xBB, 0xBF });

            var transcript = await File.ReadAllTextAsync(Path.Combine(output, "transcripts", "ky.hyp.txt"), Encoding.UTF8);
            transcript.ShouldContain("[00:00:00.500 – 00:00:03.500] уйдо");
            transcript.ShouldContain("не является протоколом");
            (await File.ReadAllTextAsync(Path.Combine(output, "transcripts", "ky.ref.words.txt"))).ShouldBe("үйдө\nбала\nжок\n");
            (await File.ReadAllTextAsync(Path.Combine(output, "transcripts", "ky.hyp.words.txt"))).ShouldBe("уйдо\nбала\nжок\n");

            await Should.ThrowAsync<ArgumentException>(
                () => SpeechEvalReportExporter.ExportAsync(report, Path.Combine(DatasetFolder, "out"), csvNumberFormat: CultureInfo.InvariantCulture));
        }
        finally
        {
            if (Directory.Exists(output))
            {
                Directory.Delete(output, recursive: true);
            }
        }
    }

    private static SpeechEvalReport SampleReport()
    {
        var kyrgyz = Result(
            Recording("ky.ogg", SpeechLanguage.Kyrgyz, RecordingCondition.Phone, "Үйдө бала жок.", "шум | ветер; рынок"),
            [new TranscriptSegmentDraft(0, 500, 3_500, "уйдо"), new TranscriptSegmentDraft(1, 4_000, 6_000, "бала жок")],
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(30));
        var russian = Result(
            Recording("ru.wav", SpeechLanguage.Russian, RecordingCondition.Interview, "Всё верно.", string.Empty),
            [new TranscriptSegmentDraft(0, 0, 1_000, "все верно")],
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(10));
        var broken = new SpeechEvalRecordResult(
            Recording("broken.wav", SpeechLanguage.Russian, RecordingCondition.Phone, "нет", string.Empty),
            [], string.Empty, "нет", string.Empty, default, default, TimeSpan.FromSeconds(1), null, DurationSource.Unknown,
            "InvalidOperationException: сбой");

        // Контрольный файл без речи: 10 минут гула, модель «услышала» одно слово.
        var hum = Result(
            Recording("hum.wav", language: null, RecordingCondition.Other, string.Empty, "гул"),
            [new TranscriptSegmentDraft(0, 60_000, 61_000, "ага")],
            TimeSpan.FromSeconds(20),
            TimeSpan.FromMinutes(10));

        return new SpeechEvalReport(
            "fake-asr@0123456789ab",
            DatasetFolder,
            new DateTimeOffset(2026, 9, 25, 8, 0, 0, TimeSpan.Zero),
            TimeSpan.FromSeconds(5),
            "тестовая машина",
            [kyrgyz, russian, broken, hum],
            ["ky.ogg: числа цифрами"]);
    }

    private static SpeechEvalRecording Recording(string file, SpeechLanguage? language, RecordingCondition condition, string reference, string note) =>
        new(file, Path.Combine(DatasetFolder, file), Path.Combine(DatasetFolder, Path.ChangeExtension(file, ".txt")),
            language, condition, note, reference);

    private static SpeechEvalRecordResult Result(
        SpeechEvalRecording recording, TranscriptSegmentDraft[] segments, TimeSpan elapsed, TimeSpan duration)
    {
        var hypothesis = string.Join(' ', segments.Select(segment => segment.Text));
        return new SpeechEvalRecordResult(
            recording,
            segments,
            hypothesis,
            SpeechTextNormalizer.Normalize(recording.ReferenceText),
            SpeechTextNormalizer.Normalize(hypothesis),
            ErrorRateCalculator.WordErrors(recording.ReferenceText, hypothesis),
            ErrorRateCalculator.CharacterErrors(recording.ReferenceText, hypothesis),
            elapsed,
            duration,
            DurationSource.Measured,
            Error: null);
    }
}
