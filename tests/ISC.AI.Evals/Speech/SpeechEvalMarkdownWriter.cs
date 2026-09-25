using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace ISC.AI.Evals.Speech;

/// <summary>
/// Отчёт прогона в Markdown (КИ-10): шапка с моделью, источником гипотез и машиной, методика, сводки по языку,
/// условиям и их сочетанию, худшие записи, все оцениваемые речевые записи, записи с большой долей
/// неразборчивого, записи «только для скорости», контрольные файлы без речи, сбои и замечания к набору.
/// </summary>
/// <remarks>
/// РЕЖИМ. Отчёт содержит имена файлов, примечания и примеры слов эталонов из замечаний — это сведения
/// материалов дел: он хранится там же, где набор (в контуре), не в репозитории. В протокол пилота, который
/// выходит за пределы группы с допуском, переносятся только числа (методика пилота, раздел 2).
/// </remarks>
public static class SpeechEvalMarkdownWriter
{
    /// <summary>Сколько худших записей показывать по умолчанию.</summary>
    public const int DefaultWorstCount = 5;

    /// <summary>Отчёт строкой.</summary>
    /// <param name="report">Итог прогона.</param>
    /// <param name="worstCount">Сколько худших записей показать.</param>
    public static string ToMarkdown(SpeechEvalReport report, int worstCount = DefaultWorstCount)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        Write(report, writer, worstCount);
        return writer.ToString();
    }

    /// <summary>Пишет отчёт в <paramref name="writer"/>.</summary>
    /// <param name="report">Итог прогона.</param>
    /// <param name="writer">Куда писать.</param>
    /// <param name="worstCount">Сколько худших записей показать.</param>
    public static void Write(SpeechEvalReport report, TextWriter writer, int worstCount = DefaultWorstCount)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(writer);

        var records = report.Records;
        var failed = records.Where(record => !record.Succeeded).ToList();
        var speech = records.Where(record => !record.Recording.IsNoSpeech && !record.Recording.IsSpeedOnly).ToList();

        writer.WriteLine("# Оценка расшифровки речи");
        writer.WriteLine();
        writer.WriteLine("| | |");
        writer.WriteLine("|---|---|");
        writer.WriteLine($"| Модель | `{SpeechEvalFormat.Cell(report.ModelVersion)}` |");
        writer.WriteLine(report.HypothesisFolder is null
            ? "| Гипотезы | распознаватель системы (порт IAudioTranscriber); время и RTF измерены |"
            : $"| Гипотезы | готовый выход модели из `{SpeechEvalFormat.Cell(report.HypothesisFolder)}`; время и RTF не измерялись |");
        writer.WriteLine($"| Набор | `{SpeechEvalFormat.Cell(report.DatasetFolder)}` — записей {SpeechEvalFormat.Count(records.Count)}, оценено {SpeechEvalFormat.Count(records.Count - failed.Count)}, сбоев {SpeechEvalFormat.Count(failed.Count)} |");
        writer.WriteLine($"| Прогон | {report.StartedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC, длительность {SpeechEvalFormat.Duration(report.TotalElapsed)} |");
        writer.WriteLine($"| Машина | {SpeechEvalFormat.Cell(report.Machine)} |");
        writer.WriteLine();
        writer.WriteLine("**Как считать.** Эталон и выход модели нормализуются одинаково: нижний регистр, знаки препинания и дефис — "
            + "границы слов, ё → е, пробелы схлопнуты; киргизские ң, ө, ү — отдельные буквы (не н, о, у); числа не нормализуются. "
            + "WER — ошибки по словам, CER — по символам: (S + D + I) / N, где S — замены, D — пропуски модели, "
            + "I — лишние слова модели, N — длина эталона. Участок `[неразборчиво]` в эталоне не входит в N, а слова модели "
            + "на его месте не считаются ни ошибками, ни совпадениями (столбец «Не оцен.»); в CER участок поглощает только "
            + "целые слова модели, буквы, приклеенные к соседнему слову, — ошибки. WER и CER групп — по корпусу группы "
            + "(Σ ошибок / Σ длины эталона), т. е. среднее по записям, взвешенное по числу слов (символов) эталона. "
            + "Записи с эталоном из одних пометок (длинные записи целиком) в сводки качества не входят — они в разделе "
            + "«Только скорость». Контрольные файлы без речи (язык «-») в WER не входят: по ним считаются ложные слова и фрагменты. "
            + "RTF = время распознавания / длительность звука, включая запуск процесса и загрузку модели (на коротких записях "
            + "завышен); «≈» — длительность оценена по концу последнего фрагмента. Общего WER по всем языкам нет намеренно: "
            + "русский не должен маскировать киргизский и смешанную речь (КИ-03).");
        writer.WriteLine();

        writer.WriteLine("## По языку");
        writer.WriteLine();
        WriteGroups(writer, "Язык", SpeechEvalAggregator.ByLanguage(records));

        writer.WriteLine("## По условиям записи");
        writer.WriteLine();
        writer.WriteLine("Языки здесь смешаны — читать вместе с таблицей «язык × условия».");
        writer.WriteLine();
        WriteGroups(writer, "Условия", SpeechEvalAggregator.ByCondition(records));

        writer.WriteLine("## Язык × условия");
        writer.WriteLine();
        WriteGroups(writer, "Язык, условия", SpeechEvalAggregator.ByLanguageAndCondition(records));

        var worst = SpeechEvalAggregator.Worst(records, worstCount);
        writer.WriteLine("## Худшие записи (по WER)");
        writer.WriteLine();
        if (worst.Count == 0)
        {
            writer.WriteLine("Записей с ошибками нет.");
            writer.WriteLine();
        }
        else
        {
            WriteRecords(writer, worst);
        }

        writer.WriteLine("## Все записи");
        writer.WriteLine();
        if (speech.Count == 0)
        {
            writer.WriteLine("Речевых записей в прогоне нет.");
            writer.WriteLine();
        }
        else
        {
            WriteRecords(writer, speech);
        }

        WriteUnintelligibleHeavy(writer, SpeechEvalAggregator.UnintelligibleHeavy(records));

        if (SpeechEvalAggregator.SpeedOnly(records) is { } speedOnly)
        {
            WriteSpeedOnly(writer, speedOnly, records.Where(record => record.Recording.IsSpeedOnly));
        }

        if (SpeechEvalAggregator.NoSpeech(records) is { } noSpeech)
        {
            WriteNoSpeech(writer, noSpeech, records.Where(record => record.Recording.IsNoSpeech));
        }

        if (failed.Count > 0)
        {
            writer.WriteLine("## Сбои распознавания");
            writer.WriteLine();
            foreach (var record in failed)
            {
                writer.WriteLine($"- `{SpeechEvalFormat.Cell(record.Recording.File)}`: {SpeechEvalFormat.Cell(record.Error)}");
            }

            writer.WriteLine();
        }

        if (report.DatasetWarnings.Count > 0)
        {
            writer.WriteLine("## Замечания к набору");
            writer.WriteLine();
            writer.WriteLine("Не мешают прогону, но могут исказить метрики или состав оценки — поправьте набор и повторите прогон.");
            writer.WriteLine();
            foreach (var warning in report.DatasetWarnings)
            {
                writer.WriteLine($"- {SpeechEvalFormat.Cell(warning)}");
            }

            writer.WriteLine();
        }
    }

    private static void WriteGroups(TextWriter writer, string title, IReadOnlyList<SpeechEvalGroupSummary> groups)
    {
        writer.WriteLine($"| {title} | Записей | Слов эталона | Нрзб. | WER | S | D | I | Не оцен. | CER | Звук | Время | RTF |");
        writer.WriteLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var group in groups)
        {
            if (group.Records == 0)
            {
                writer.WriteLine($"| {SpeechEvalFormat.Cell(group.DisplayName)} | 0 | {SpeechEvalFormat.Missing} | | нет записей | | | | | | | | |");
                continue;
            }

            if (group.Evaluated == 0)
            {
                writer.WriteLine($"| {SpeechEvalFormat.Cell(group.DisplayName)} | {SpeechEvalFormat.Count(group.Records)} (сбоев {SpeechEvalFormat.Count(group.Failed)}) | {SpeechEvalFormat.Missing} | | все со сбоем | | | | | | | | |");
                continue;
            }

            var records = group.Failed > 0
                ? $"{SpeechEvalFormat.Count(group.Records)} (сбоев {SpeechEvalFormat.Count(group.Failed)})"
                : SpeechEvalFormat.Count(group.Records);
            writer.WriteLine(
                $"| {SpeechEvalFormat.Cell(group.DisplayName)} | {records} | {SpeechEvalFormat.Count(group.Words.ReferenceLength)} "
                + $"| {SpeechEvalFormat.Count(group.UnintelligibleRegions)} "
                + $"| {SpeechEvalFormat.Percent(group.WordErrorRate)} | {SpeechEvalFormat.Count(group.Words.Substitutions)} "
                + $"| {SpeechEvalFormat.Count(group.Words.Deletions)} | {SpeechEvalFormat.Count(group.Words.Insertions)} "
                + $"| {SpeechEvalFormat.Count(group.Words.Unscored)} "
                + $"| {SpeechEvalFormat.Percent(group.CharacterErrorRate)} | {SpeechEvalFormat.Duration(group.Audio)} "
                + $"| {SpeechEvalFormat.Duration(group.Elapsed)} | {SpeechEvalFormat.Factor(group.RealTimeFactor, group.DurationApproximate)} |");
        }

        writer.WriteLine();
    }

    private static void WriteRecords(TextWriter writer, IEnumerable<SpeechEvalRecordResult> records)
    {
        writer.WriteLine("| Файл | Язык | Условия | Слов эталона | Нрзб. | WER | S | D | I | Не оцен. | CER | Звук | Время | RTF | Примечание |");
        writer.WriteLine("|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|");
        foreach (var record in records)
        {
            var recording = record.Recording;
            var prefix = $"| `{SpeechEvalFormat.Cell(recording.File)}` | {recording.Language.ToCode()} | {recording.Condition.ToCode()} ";
            if (!record.Succeeded)
            {
                writer.WriteLine(
                    $"{prefix}| {SpeechEvalFormat.Count(SpeechTextNormalizer.CountScoredReferenceWords(recording.ReferenceText))} "
                    + $"| {SpeechEvalFormat.Count(recording.UnintelligibleRegions)} | сбой | | | | | | | | | {SpeechEvalFormat.Cell(recording.Note)} |");
                continue;
            }

            writer.WriteLine(
                $"{prefix}| {SpeechEvalFormat.Count(record.Words.ReferenceLength)} | {SpeechEvalFormat.Count(recording.UnintelligibleRegions)} "
                + $"| {SpeechEvalFormat.Percent(record.WordErrorRate)} "
                + $"| {SpeechEvalFormat.Count(record.Words.Substitutions)} | {SpeechEvalFormat.Count(record.Words.Deletions)} "
                + $"| {SpeechEvalFormat.Count(record.Words.Insertions)} | {SpeechEvalFormat.Count(record.Words.Unscored)} "
                + $"| {SpeechEvalFormat.Percent(record.CharacterErrorRate)} "
                + $"| {SpeechEvalFormat.Duration(record.AudioDuration)} | {ElapsedCell(record)} "
                + $"| {SpeechEvalFormat.Factor(record.RealTimeFactor, record.DurationSource == DurationSource.LastSegmentEnd)} "
                + $"| {SpeechEvalFormat.Cell(recording.Note)} |");
        }

        writer.WriteLine();
    }

    private static void WriteUnintelligibleHeavy(TextWriter writer, IReadOnlyList<SpeechEvalRecordResult> records)
    {
        writer.WriteLine("## Записи с большой долей неразборчивого");
        writer.WriteLine();
        writer.WriteLine($"Больше одной пометки `[неразборчиво]` на {SpeechEvalAggregator.WordsPerUnintelligibleMark} слов эталона "
            + "(методика 4.6): WER таких записей посчитан только по разобранной речи, качество на трудных местах не измерено. "
            + "В сводки и пороги они входят; в протокол (10.1, раздел 2) переносятся их идентификаторы.");
        writer.WriteLine();
        if (records.Count == 0)
        {
            writer.WriteLine("Таких записей нет.");
            writer.WriteLine();
            return;
        }

        writer.WriteLine("| Файл | Язык | Условия | Слов эталона | Нрзб. | WER |");
        writer.WriteLine("|---|---|---|---:|---:|---:|");
        foreach (var record in records)
        {
            var recording = record.Recording;
            writer.WriteLine(
                $"| `{SpeechEvalFormat.Cell(recording.File)}` | {recording.Language.ToCode()} | {recording.Condition.ToCode()} "
                + $"| {SpeechEvalFormat.Count(SpeechTextNormalizer.CountScoredReferenceWords(recording.ReferenceText))} "
                + $"| {SpeechEvalFormat.Count(recording.UnintelligibleRegions)} "
                + $"| {(record.Succeeded ? SpeechEvalFormat.Percent(record.WordErrorRate) : "сбой")} |");
        }

        writer.WriteLine();
    }

    private static void WriteSpeedOnly(TextWriter writer, SpeechEvalGroupSummary summary, IEnumerable<SpeechEvalRecordResult> records)
    {
        writer.WriteLine("## Только скорость");
        writer.WriteLine();
        writer.WriteLine("Эталон состоит только из пометок `[неразборчиво]` — по методике (7.2) это длинные записи целиком: "
            + "качество по ним не оценивается, в сводки выше (записей, WER/CER, «Не оцен.») они не входят. По ним — время, "
            + "длительность и RTF (методика 6.4).");
        writer.WriteLine();

        var failed = summary.Failed > 0 ? $" (сбоев {SpeechEvalFormat.Count(summary.Failed)})" : string.Empty;
        writer.WriteLine(
            $"**Итого:** записей {SpeechEvalFormat.Count(summary.Records)}{failed}, звук {SpeechEvalFormat.Duration(summary.Audio)}, "
            + $"время {SpeechEvalFormat.Duration(summary.Elapsed)}, RTF {SpeechEvalFormat.Factor(summary.RealTimeFactor, summary.DurationApproximate)}.");
        writer.WriteLine();

        writer.WriteLine("| Файл | Язык | Условия | Звук | Время | RTF | Слов модели | Фрагментов с текстом | Примечание |");
        writer.WriteLine("|---|---|---|---:|---:|---:|---:|---:|---|");
        foreach (var record in records)
        {
            var recording = record.Recording;
            var prefix = $"| `{SpeechEvalFormat.Cell(recording.File)}` | {recording.Language.ToCode()} | {recording.Condition.ToCode()} ";
            if (!record.Succeeded)
            {
                writer.WriteLine($"{prefix}| {SpeechEvalFormat.Duration(record.AudioDuration)} | сбой | | | | {SpeechEvalFormat.Cell(recording.Note)} |");
                continue;
            }

            writer.WriteLine(
                $"{prefix}| {SpeechEvalFormat.Duration(record.AudioDuration)} | {ElapsedCell(record)} "
                + $"| {SpeechEvalFormat.Factor(record.RealTimeFactor, record.DurationSource == DurationSource.LastSegmentEnd)} "
                + $"| {SpeechEvalFormat.Count(record.Words.HypothesisLength)} | {SpeechEvalFormat.Count(record.FragmentsWithText)} "
                + $"| {SpeechEvalFormat.Cell(recording.Note)} |");
        }

        writer.WriteLine();
    }

    private static void WriteNoSpeech(TextWriter writer, SpeechEvalNoSpeechSummary summary, IEnumerable<SpeechEvalRecordResult> records)
    {
        writer.WriteLine("## Контрольные файлы без речи");
        writer.WriteLine();
        writer.WriteLine("Тишина, музыка, шум: всё, что выдала модель, — ложное. В WER не входит. Текст ложных фрагментов — "
            + "в `transcripts/<имя>.hyp.txt`: правдоподобная «фраза» из ничего опаснее бессмысленного набора букв.");
        writer.WriteLine();

        var failed = summary.Failed > 0 ? $" (сбоев {SpeechEvalFormat.Count(summary.Failed)})" : string.Empty;
        var unmeasured = summary.UnmeasuredRecords > 0
            ? $"; без измеренной длительности {SpeechEvalFormat.Count(summary.UnmeasuredRecords)} — в счёт «на 10 минут» не входят"
            : string.Empty;
        writer.WriteLine(
            $"**Ложные слова и фрагменты:** файлов {SpeechEvalFormat.Count(summary.Records)}{failed}, звука {SpeechEvalFormat.Duration(summary.MeasuredAudio)}; "
            + $"ложных слов {SpeechEvalFormat.Count(summary.FalseWords)}, ложных фрагментов {SpeechEvalFormat.Count(summary.FalseFragments)}; "
            + $"на 10 минут — фрагментов {SpeechEvalFormat.Number(summary.FalseFragmentsPer10Minutes)}, слов {SpeechEvalFormat.Number(summary.FalseWordsPer10Minutes)}{unmeasured}.");
        writer.WriteLine();

        writer.WriteLine("| Файл | Условия | Звук | Ложных слов | Ложных фрагментов | Примечание |");
        writer.WriteLine("|---|---|---:|---:|---:|---|");
        foreach (var record in records)
        {
            var recording = record.Recording;
            var counts = record.Succeeded
                ? $"{SpeechEvalFormat.Count(record.Words.HypothesisLength)} | {SpeechEvalFormat.Count(record.FragmentsWithText)}"
                : "сбой | ";
            writer.WriteLine(
                $"| `{SpeechEvalFormat.Cell(recording.File)}` | {recording.Condition.ToCode()} | {SpeechEvalFormat.Duration(record.AudioDuration)} "
                + $"| {counts} | {SpeechEvalFormat.Cell(recording.Note)} |");
        }

        writer.WriteLine();
    }

    private static string ElapsedCell(SpeechEvalRecordResult record) =>
        record.Precomputed ? SpeechEvalFormat.Missing : SpeechEvalFormat.Duration(record.Elapsed);
}
