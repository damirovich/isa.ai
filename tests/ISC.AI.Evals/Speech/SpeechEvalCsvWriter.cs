using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace ISC.AI.Evals.Speech;

/// <summary>
/// Отчёт прогона в CSV для дальнейшего анализа и сравнения моделей: по записям (<see cref="WriteRecords"/>) и
/// сводки (<see cref="WriteSummary"/>). В каждой строке — версия модели, поэтому CSV разных прогонов можно
/// склеить в одну таблицу.
/// </summary>
/// <remarks>
/// Разделитель — точка с запятой (как у манифеста и русского Excel). Доли (WER, CER) — долями, не
/// процентами; числа по умолчанию в инвариантной культуре (точка). Для открытия в русском Excel передайте
/// культуру с запятой (например, <c>ru-RU</c>) — разделитель полей с ней не конфликтует.
/// </remarks>
public static class SpeechEvalCsvWriter
{
    private const char Separator = ';';

    private static readonly char[] CharactersToQuote = [Separator, '"', '\r', '\n'];

    private static readonly string[] RecordColumns =
    [
        "model", "file", "language", "condition", "note", "status", "error", "segments",
        "ref_words", "hyp_words", "wer", "word_sub", "word_del", "word_ins",
        "ref_chars", "hyp_chars", "cer", "char_sub", "char_del", "char_ins",
        "audio_seconds", "duration_source", "elapsed_seconds", "rtf",
        "unintelligible", "word_unscored", "char_unscored", "text_segments", "hypothesis_source",
    ];

    private static readonly string[] SummaryColumns =
    [
        "model", "group_by", "group", "records", "failed",
        "ref_words", "wer", "word_sub", "word_del", "word_ins",
        "ref_chars", "cer", "char_sub", "char_del", "char_ins",
        "audio_seconds", "elapsed_seconds", "rtf", "rtf_approximate",
        "word_unscored", "false_words", "false_fragments", "unintelligible",
    ];

    /// <summary>
    /// Строка на каждую запись набора, включая сбойные (<c>status=failed</c>) и контрольные файлы без речи
    /// (<c>language=-</c>: <c>hyp_words</c> — ложные слова, <c>text_segments</c> — ложные фрагменты).
    /// </summary>
    /// <param name="report">Итог прогона.</param>
    /// <param name="writer">Куда писать.</param>
    /// <param name="numberFormat">Культура чисел; по умолчанию — инвариантная.</param>
    public static void WriteRecords(SpeechEvalReport report, TextWriter writer, IFormatProvider? numberFormat = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(writer);

        var format = numberFormat ?? CultureInfo.InvariantCulture;
        WriteRow(writer, RecordColumns);
        foreach (var record in report.Records)
        {
            var recording = record.Recording;
            var ok = record.Succeeded;
            WriteRow(writer,
            [
                report.ModelVersion,
                recording.File,
                recording.Language.ToCode(),
                recording.Condition.ToCode(),
                recording.Note,
                ok ? "ok" : "failed",
                record.Error ?? string.Empty,
                Integer(record.Segments.Count, format),
                ok ? Integer(record.Words.ReferenceLength, format) : string.Empty,
                ok ? Integer(record.Words.HypothesisLength, format) : string.Empty,
                SpeechEvalFormat.Csv(record.WordErrorRate, "0.0000", format),
                ok ? Integer(record.Words.Substitutions, format) : string.Empty,
                ok ? Integer(record.Words.Deletions, format) : string.Empty,
                ok ? Integer(record.Words.Insertions, format) : string.Empty,
                ok ? Integer(record.Characters.ReferenceLength, format) : string.Empty,
                ok ? Integer(record.Characters.HypothesisLength, format) : string.Empty,
                SpeechEvalFormat.Csv(record.CharacterErrorRate, "0.0000", format),
                ok ? Integer(record.Characters.Substitutions, format) : string.Empty,
                ok ? Integer(record.Characters.Deletions, format) : string.Empty,
                ok ? Integer(record.Characters.Insertions, format) : string.Empty,
                SpeechEvalFormat.Csv(record.AudioDuration?.TotalSeconds, "0.000", format),
                DurationSourceCode(record.DurationSource),
                SpeechEvalFormat.Csv(record.Precomputed ? null : record.Elapsed.TotalSeconds, "0.000", format),
                SpeechEvalFormat.Csv(ok ? record.RealTimeFactor : null, "0.0000", format),
                Integer(recording.UnintelligibleRegions, format),
                ok ? Integer(record.Words.Unscored, format) : string.Empty,
                ok ? Integer(record.Characters.Unscored, format) : string.Empty,
                ok ? Integer(record.FragmentsWithText, format) : string.Empty,
                record.Precomputed ? "precomputed" : "transcriber",
            ]);
        }
    }

    /// <summary>
    /// Сводки: по языку (<c>group_by=language</c>), условиям (<c>condition</c>) и их сочетанию
    /// (<c>language_condition</c>), с числом пометок <c>[неразборчиво]</c> группы (<c>unintelligible</c>); записи
    /// «только для скорости» — строка <c>group_by=speed_only</c> (записей,
    /// сбоев, звук, время, RTF; полей качества нет); контрольные файлы без речи — строка <c>group_by=no_speech</c>
    /// с ложными словами и фрагментами (<c>false_words</c>, <c>false_fragments</c>) вместо WER.
    /// </summary>
    /// <param name="report">Итог прогона.</param>
    /// <param name="writer">Куда писать.</param>
    /// <param name="numberFormat">Культура чисел; по умолчанию — инвариантная.</param>
    public static void WriteSummary(SpeechEvalReport report, TextWriter writer, IFormatProvider? numberFormat = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(writer);

        var format = numberFormat ?? CultureInfo.InvariantCulture;
        WriteRow(writer, SummaryColumns);
        var groups = SpeechEvalAggregator.ByLanguage(report.Records).Select(group => ("language", group))
            .Concat(SpeechEvalAggregator.ByCondition(report.Records).Select(group => ("condition", group)))
            .Concat(SpeechEvalAggregator.ByLanguageAndCondition(report.Records).Select(group => ("language_condition", group)));

        foreach (var (groupBy, group) in groups)
        {
            WriteRow(writer,
            [
                report.ModelVersion,
                groupBy,
                group.Key,
                Integer(group.Records, format),
                Integer(group.Failed, format),
                Integer(group.Words.ReferenceLength, format),
                SpeechEvalFormat.Csv(group.WordErrorRate, "0.0000", format),
                Integer(group.Words.Substitutions, format),
                Integer(group.Words.Deletions, format),
                Integer(group.Words.Insertions, format),
                Integer(group.Characters.ReferenceLength, format),
                SpeechEvalFormat.Csv(group.CharacterErrorRate, "0.0000", format),
                Integer(group.Characters.Substitutions, format),
                Integer(group.Characters.Deletions, format),
                Integer(group.Characters.Insertions, format),
                SpeechEvalFormat.Csv(group.Audio.TotalSeconds, "0.000", format),
                SpeechEvalFormat.Csv(group.Elapsed?.TotalSeconds, "0.000", format),
                SpeechEvalFormat.Csv(group.RealTimeFactor, "0.0000", format),
                group.DurationApproximate ? "yes" : "no",
                Integer(group.Words.Unscored, format),
                string.Empty,
                string.Empty,
                Integer(group.UnintelligibleRegions, format),
            ]);
        }

        // Записи «только для скорости» — отдельной строкой: в сводки качества не входят, у них только скорость.
        if (SpeechEvalAggregator.SpeedOnly(report.Records) is { } speedOnly)
        {
            WriteRow(writer,
            [
                report.ModelVersion,
                "speed_only",
                speedOnly.Key,
                Integer(speedOnly.Records, format),
                Integer(speedOnly.Failed, format),
                string.Empty, string.Empty, string.Empty, string.Empty, string.Empty,
                string.Empty, string.Empty, string.Empty, string.Empty, string.Empty,
                SpeechEvalFormat.Csv(speedOnly.Audio.TotalSeconds, "0.000", format),
                SpeechEvalFormat.Csv(speedOnly.Elapsed?.TotalSeconds, "0.000", format),
                SpeechEvalFormat.Csv(speedOnly.RealTimeFactor, "0.0000", format),
                speedOnly.DurationApproximate ? "yes" : "no",
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
            ]);
        }

        // Контрольные файлы без речи — отдельной строкой: в WER не входят, у них свои показатели.
        if (SpeechEvalAggregator.NoSpeech(report.Records) is { } noSpeech)
        {
            WriteRow(writer,
            [
                report.ModelVersion,
                "no_speech",
                SpeechEvalCodes.NoSpeechCode,
                Integer(noSpeech.Records, format),
                Integer(noSpeech.Failed, format),
                string.Empty, string.Empty, string.Empty, string.Empty, string.Empty,
                string.Empty, string.Empty, string.Empty, string.Empty, string.Empty,
                SpeechEvalFormat.Csv(noSpeech.MeasuredAudio.TotalSeconds, "0.000", format),
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                Integer(noSpeech.FalseWords, format),
                Integer(noSpeech.FalseFragments, format),
                string.Empty,
            ]);
        }
    }

    /// <summary>Экранирует поле CSV: кавычки, если в нём разделитель, кавычка или перевод строки.</summary>
    /// <param name="value">Значение поля.</param>
    public static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.IndexOfAny(CharactersToQuote) >= 0
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;
    }

    private static void WriteRow(TextWriter writer, IReadOnlyList<string> fields) =>
        writer.WriteLine(string.Join(Separator, fields.Select(Escape)));

    private static string Integer(int value, IFormatProvider format) => value.ToString(format);

    private static string DurationSourceCode(DurationSource source) => source switch
    {
        DurationSource.Measured => "measured",
        DurationSource.Transcriber => "transcriber",
        DurationSource.LastSegmentEnd => "last_segment_end",
        _ => "unknown",
    };
}
