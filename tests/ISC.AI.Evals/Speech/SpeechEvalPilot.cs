using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Modules.Media.Domain.Services;

namespace ISC.AI.Evals.Speech;

/// <summary>
/// Прогон пилота одной командой (КИ-10, ADR-0026): загрузить набор → получить гипотезы → посчитать метрики →
/// записать отчёт. Две точки входа: <see cref="RunAndExportAsync"/> — через распознаватель системы (GigaAM
/// 220M/600M), <see cref="RunPrecomputedAndExportAsync"/> — по готовому выходу сторонней модели
/// (Vosk, Whisper) из <c>&lt;набор&gt;/hyp/&lt;модель&gt;/</c>.
/// </summary>
/// <remarks>
/// Сравнение моделей — несколько вызовов с РАЗНЫМИ папками отчёта; CSV прогонов склеиваются по колонке
/// <c>model</c>. Методика пилота — <c>docs/следствие/Пилот_расшифровки_речи.md</c>.
/// </remarks>
public static class SpeechEvalPilot
{
    /// <summary>Загружает набор, прогоняет его через распознаватель и пишет отчёт в папку.</summary>
    /// <param name="transcriber">Распознаватель (например, рабочая реализация <c>ISC.AI.Speech</c> с нужной моделью).</param>
    /// <param name="datasetFolder">Папка эталонного набора с <c>manifest.csv</c>.</param>
    /// <param name="outputFolder">Папка отчёта: вне папки набора, для каждой модели — своя.</param>
    /// <param name="ffprobeFolder">Каталог ffprobe для длительности не-WAV записей; <see langword="null"/> — PATH.</param>
    /// <param name="progress">Ход прогона (необязателен).</param>
    /// <param name="cancellationToken">Отмена.</param>
    /// <exception cref="SpeechEvalDatasetException">Набор непригоден (перечислены все проблемы).</exception>
    /// <exception cref="ArgumentException">Папка отчёта внутри папки набора (проверяется ДО прогона).</exception>
    public static async Task<SpeechEvalReport> RunAndExportAsync(
        IAudioTranscriber transcriber,
        string datasetFolder,
        string outputFolder,
        string? ffprobeFolder = null,
        IProgress<SpeechEvalProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transcriber);

        var dataset = await SpeechEvalDataset.LoadAsync(datasetFolder, cancellationToken).ConfigureAwait(false);
        SpeechEvalReportExporter.EnsureOutputFolderAllowed(outputFolder, dataset.RootFolder);

        var runner = new SpeechEvalRunner(transcriber, new AudioDurationProbe(ffprobeFolder));
        return await RunAndExportAsync(runner, dataset, outputFolder, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Загружает набор и готовый выход сторонней модели из <c>&lt;набор&gt;/hyp/&lt;модель&gt;/</c>, считает метрики
    /// тем же способом, что для GigaAM, и пишет отчёт в папку. Распознаватель не вызывается; время и RTF не
    /// измеряются; записи без файла готового выхода не оцениваются и перечисляются в замечаниях отчёта.
    /// </summary>
    /// <param name="datasetFolder">Папка эталонного набора с <c>manifest.csv</c> и подпапкой <c>hyp</c>.</param>
    /// <param name="model">Имя модели — имя подпапки в <c>hyp</c> (например, <c>vosk-ky-0.42</c>); попадает в колонку <c>model</c>.</param>
    /// <param name="outputFolder">Папка отчёта: вне папки набора, для каждой модели — своя.</param>
    /// <param name="ffprobeFolder">Каталог ffprobe для длительности не-WAV записей; <see langword="null"/> — PATH.</param>
    /// <param name="progress">Ход прогона (необязателен).</param>
    /// <param name="cancellationToken">Отмена.</param>
    /// <exception cref="SpeechEvalDatasetException">Набор или папка готового выхода непригодны (перечислены все проблемы).</exception>
    /// <exception cref="ArgumentException">Папка отчёта внутри папки набора или негодное имя модели.</exception>
    public static async Task<SpeechEvalReport> RunPrecomputedAndExportAsync(
        string datasetFolder,
        string model,
        string outputFolder,
        string? ffprobeFolder = null,
        IProgress<SpeechEvalProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var dataset = await SpeechEvalDataset.LoadAsync(datasetFolder, cancellationToken).ConfigureAwait(false);
        SpeechEvalReportExporter.EnsureOutputFolderAllowed(outputFolder, dataset.RootFolder);
        var hypotheses = await SpeechEvalExternalHypotheses.LoadAsync(dataset, model, cancellationToken).ConfigureAwait(false);

        var runner = new SpeechEvalRunner(hypotheses, new AudioDurationProbe(ffprobeFolder));
        return await RunAndExportAsync(runner, dataset, outputFolder, progress, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<SpeechEvalReport> RunAndExportAsync(
        SpeechEvalRunner runner,
        SpeechEvalDataset dataset,
        string outputFolder,
        IProgress<SpeechEvalProgress>? progress,
        CancellationToken cancellationToken)
    {
        var report = await runner.RunAsync(dataset, progress, cancellationToken).ConfigureAwait(false);
        await SpeechEvalReportExporter.ExportAsync(
            report, outputFolder, csvNumberFormat: CultureInfo.InvariantCulture, cancellationToken: cancellationToken).ConfigureAwait(false);
        return report;
    }
}
