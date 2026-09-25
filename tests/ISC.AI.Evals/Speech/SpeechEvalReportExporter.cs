using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ISC.AI.Evals.Speech;

/// <summary>
/// Запись отчёта прогона в папку: <c>report.md</c>, <c>records.csv</c>, <c>summary.csv</c> и по каждой записи —
/// расшифровка с таймкодами и два файла «по слову в строке» для сравнения эталона с выходом модели.
/// </summary>
/// <remarks>
/// <para>ЗАЧЕМ ФАЙЛЫ «ПО СЛОВУ В СТРОКЕ». <c>&lt;имя&gt;.ref.words.txt</c> и <c>&lt;имя&gt;.hyp.words.txt</c> —
/// нормализованные эталон и гипотеза, одно слово на строку. Любая программа сравнения (WinMerge,
/// <c>git diff --no-index</c>) показывает по ним, КАКИЕ слова модель заменила, пропустила или добавила —
/// имена, топонимы, числа, киргизские слова — без отдельного инструмента выравнивания. Пометка
/// <c>[неразборчиво]</c> остаётся в эталоне отдельной строкой: слова модели напротив неё не оценивались.</para>
/// <para>РЕЖИМ. В папке отчёта — дословные расшифровки реальных записей, т. е. материалы дел: папка живёт
/// только в контуре, не в репозитории, и удаляется по окончании пилота вместе с набором. Внутрь папки набора
/// отчёт не пишется: гипотезы <c>.txt</c> перемешались бы с эталонами.</para>
/// </remarks>
public static class SpeechEvalReportExporter
{
    /// <summary>Имя Markdown-отчёта.</summary>
    public const string MarkdownFileName = "report.md";

    /// <summary>Имя CSV по записям.</summary>
    public const string RecordsCsvFileName = "records.csv";

    /// <summary>Имя CSV со сводками.</summary>
    public const string SummaryCsvFileName = "summary.csv";

    /// <summary>Подпапка с расшифровками записей.</summary>
    public const string TranscriptsFolderName = "transcripts";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    // CSV — с BOM: иначе Excel откроет UTF-8 как ANSI и покажет кириллицу кракозябрами.
    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);

    /// <summary>Пишет отчёт в папку (создаётся при необходимости; одноимённые файлы перезаписываются).</summary>
    /// <param name="report">Итог прогона.</param>
    /// <param name="outputFolder">Папка отчёта — вне папки набора.</param>
    /// <param name="includeTranscripts">Писать ли расшифровки записей (<see cref="TranscriptsFolderName"/>).</param>
    /// <param name="worstCount">Сколько худших записей показать в Markdown.</param>
    /// <param name="csvNumberFormat">Культура чисел в CSV; по умолчанию — инвариантная (точка). Для русского Excel — <c>ru-RU</c>.</param>
    /// <param name="cancellationToken">Отмена.</param>
    /// <returns>Полные пути записанных файлов.</returns>
    /// <exception cref="ArgumentException">Папка отчёта совпадает с папкой набора или лежит внутри неё.</exception>
    public static async Task<IReadOnlyList<string>> ExportAsync(
        SpeechEvalReport report,
        string outputFolder,
        bool includeTranscripts = true,
        int worstCount = SpeechEvalMarkdownWriter.DefaultWorstCount,
        IFormatProvider? csvNumberFormat = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFolder);

        EnsureOutputFolderAllowed(outputFolder, report.DatasetFolder);
        var output = Path.GetFullPath(outputFolder);
        Directory.CreateDirectory(output);
        var csvFormat = csvNumberFormat ?? CultureInfo.InvariantCulture;
        var written = new List<string>();

        written.Add(await WriteAsync(Path.Combine(output, MarkdownFileName), SpeechEvalMarkdownWriter.ToMarkdown(report, worstCount), Utf8NoBom, cancellationToken).ConfigureAwait(false));
        written.Add(await WriteAsync(Path.Combine(output, RecordsCsvFileName), Render(writer => SpeechEvalCsvWriter.WriteRecords(report, writer, csvFormat)), Utf8WithBom, cancellationToken).ConfigureAwait(false));
        written.Add(await WriteAsync(Path.Combine(output, SummaryCsvFileName), Render(writer => SpeechEvalCsvWriter.WriteSummary(report, writer, csvFormat)), Utf8WithBom, cancellationToken).ConfigureAwait(false));

        if (includeTranscripts)
        {
            var transcripts = Path.Combine(output, TranscriptsFolderName);
            foreach (var record in report.Records)
            {
                // Путь записи проверяется при загрузке набора, но отчёт мог быть собран и вручную: файл
                // расшифровки не должен выйти за папку отчёта ни при каком File.
                var baseName = Path.GetFullPath(Path.Combine(transcripts, Path.ChangeExtension(record.Recording.File, null)));
                if (Path.IsPathRooted(record.Recording.File) || !IsSameOrInside(baseName, transcripts))
                {
                    throw new InvalidOperationException(
                        $"Путь записи «{record.Recording.File}» выходит за папку набора — расшифровка не записана.");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(baseName)!); // путь внутри папки отчёта: родитель есть всегда
                written.Add(await WriteAsync(baseName + ".hyp.txt", RenderTranscript(report.ModelVersion, record), Utf8NoBom, cancellationToken).ConfigureAwait(false));
                written.Add(await WriteAsync(baseName + ".ref.words.txt", OneWordPerLine(record.NormalizedReference), Utf8NoBom, cancellationToken).ConfigureAwait(false));
                written.Add(await WriteAsync(baseName + ".hyp.words.txt", OneWordPerLine(record.NormalizedHypothesis), Utf8NoBom, cancellationToken).ConfigureAwait(false));
            }
        }

        return written;
    }

    /// <summary>
    /// Проверяет папку отчёта ДО прогона (прогон длится часами — узнавать о негодной папке в конце поздно):
    /// она не должна совпадать с папкой набора или лежать внутри неё.
    /// </summary>
    /// <param name="outputFolder">Папка отчёта.</param>
    /// <param name="datasetFolder">Папка набора.</param>
    /// <exception cref="ArgumentException">Папка отчёта совпадает с папкой набора или лежит внутри неё.</exception>
    public static void EnsureOutputFolderAllowed(string outputFolder, string datasetFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetFolder);

        if (IsSameOrInside(outputFolder, datasetFolder))
        {
            throw new ArgumentException(
                $"Папка отчёта {Path.GetFullPath(outputFolder)} совпадает с папкой набора или лежит внутри неё — "
                + "расшифровки .txt перемешались бы с эталонами. Укажите другую папку.",
                nameof(outputFolder));
        }
    }

    /// <summary>
    /// Расшифровка записи с таймкодами — дословный текст модели (первичный слой) с пометкой, что это
    /// автоматическая расшифровка (ТЭ-002). У готового выхода сторонней модели таймкодов нет — фрагменты
    /// (строки её файла) пишутся без них.
    /// </summary>
    /// <param name="modelVersion">Версия модели.</param>
    /// <param name="record">Итог записи.</param>
    public static string RenderTranscript(string modelVersion, SpeechEvalRecordResult record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var builder = new StringBuilder();
        builder.AppendLine(CultureInfo.InvariantCulture, $"# запись: {record.Recording.File}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"# модель: {modelVersion}");
        builder.AppendLine("# автоматическая расшифровка, требует проверки, не является протоколом");
        if (record.Precomputed)
        {
            builder.AppendLine("# готовый выход модели: фрагмент — строка её файла, таймкодов нет");
        }

        if (record.Error is not null)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"# сбой: {record.Error}");
        }

        foreach (var segment in record.Segments)
        {
            builder.AppendLine(record.Precomputed
                ? segment.Text
                : string.Create(CultureInfo.InvariantCulture, $"[{SpeechEvalFormat.Timecode(segment.StartMs)} – {SpeechEvalFormat.Timecode(segment.EndMs)}] {segment.Text}"));
        }

        return builder.ToString();
    }

    private static string OneWordPerLine(string normalized) =>
        normalized.Length == 0 ? string.Empty : normalized.Replace(' ', '\n') + "\n";

    private static string Render(Action<TextWriter> write)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        write(writer);
        return writer.ToString();
    }

    private static async Task<string> WriteAsync(string path, string content, Encoding encoding, CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(path, content, encoding, cancellationToken).ConfigureAwait(false);
        return path;
    }

    private static bool IsSameOrInside(string path, string folder)
    {
        var normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var normalizedFolder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        return normalizedPath.Equals(normalizedFolder, StringComparison.OrdinalIgnoreCase)
            || normalizedPath.StartsWith(normalizedFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
