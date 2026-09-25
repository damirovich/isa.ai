using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Modules.Media.Domain.Model;

namespace ISC.AI.Evals.Speech;

/// <summary>
/// Готовый выход сторонней модели (Vosk, Whisper и т. п.), прогнанной на стенде пилота своей утилитой:
/// тексты <c>&lt;набор&gt;/hyp/&lt;модель&gt;/&lt;имя записи&gt;.txt</c> вместо вызова <c>IAudioTranscriber</c>
/// (методика пилота, 7.3).
/// </summary>
/// <remarks>
/// <para>ЗАЧЕМ. Сторонние модели в систему не встраиваются (ADR-0026: только кандидаты на замену), но сравнивать
/// их с GigaAM нужно ТЕМ ЖЕ инструментом: та же нормализация, тот же учёт <c>[неразборчиво]</c>, те же сводки.
/// Метрики, посчитанные другой программой, несравнимы.</para>
/// <para>ИМЕНА. Для записи <c>calls/rec-001.m4a</c> ищется <c>hyp/&lt;модель&gt;/calls/rec-001.txt</c>, а если его
/// нет — <c>hyp/&lt;модель&gt;/calls/rec-001.m4a.txt</c> (так по умолчанию называет выход whisper.cpp). Оба сразу —
/// ошибка: непонятно, какой считать.</para>
/// <para>ЧТО ИЗ ЭТОГО СЛЕДУЕТ. Каждая непустая строка файла — один фрагмент (whisper.cpp и Vosk пишут по
/// фрагменту в строке): по ним считаются ложные фрагменты на контрольных файлах. Таймкодов нет, время и RTF
/// не измеряются — их замеряют на стенде средствами самой модели. Записи без файла не оцениваются и
/// перечисляются в замечаниях (Whisper, например, прогоняется только на <c>ru</c> и контрольных файлах).</para>
/// <para>РЕЖИМ. Выход модели — дословное содержание записи того же грифа, что и сама запись: папка
/// <c>hyp</c> лежит внутри набора и уничтожается вместе с ним.</para>
/// </remarks>
public sealed class SpeechEvalExternalHypotheses
{
    /// <summary>Подпапка набора с готовым выходом сторонних моделей.</summary>
    public const string FolderName = "hyp";

    private const int MaxListed = 20;

    private SpeechEvalExternalHypotheses(
        string model, string folder, IReadOnlyDictionary<string, string> texts, IReadOnlyList<string> warnings)
    {
        Model = model;
        Folder = folder;
        Texts = texts;
        Warnings = warnings;
    }

    /// <summary>Имя модели — имя подпапки в <c>hyp</c>; попадает в отчёт вместо версии модели.</summary>
    public string Model { get; }

    /// <summary>Полный путь к папке <c>&lt;набор&gt;/hyp/&lt;модель&gt;</c>.</summary>
    public string Folder { get; }

    /// <summary>Тексты по записям набора: ключ — путь записи как в манифесте (<see cref="SpeechEvalRecording.File"/>).</summary>
    public IReadOnlyDictionary<string, string> Texts { get; }

    /// <summary>Замечания: записи без готового выхода и файлы без записи в манифесте — попадают в отчёт.</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>Читает готовый выход модели для записей набора.</summary>
    /// <param name="dataset">Загруженный эталонный набор.</param>
    /// <param name="model">Имя модели — имя подпапки в <c>hyp</c> (например, <c>vosk-ky-0.42</c>).</param>
    /// <param name="cancellationToken">Отмена.</param>
    /// <exception cref="ArgumentException">Имя модели пустое или не является именем одной папки.</exception>
    /// <exception cref="SpeechEvalDatasetException">
    /// Нет папки, ни одного подходящего файла, файл не в UTF-8 или два файла на одну запись — все проблемы сразу.
    /// </exception>
    public static async Task<SpeechEvalExternalHypotheses> LoadAsync(
        SpeechEvalDataset dataset, string model, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        model = model.Trim();
        if (model is "." or ".." || model.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || model.Contains('/', StringComparison.Ordinal) || model.Contains('\\', StringComparison.Ordinal))
        {
            throw new ArgumentException($"Имя модели «{model}» должно быть именем одной папки внутри {FolderName}.", nameof(model));
        }

        var folder = Path.Combine(dataset.RootFolder, FolderName, model);
        var shownFolder = $"{FolderName}/{model}";
        if (!Directory.Exists(folder))
        {
            throw new SpeechEvalDatasetException(
                [$"нет папки готового выхода модели {folder} (ожидается {shownFolder}/<имя записи>.txt в папке набора)"]);
        }

        var problems = new List<string>();
        var texts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        foreach (var recording in dataset.Recordings)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var candidates = CandidatePaths(folder, recording.File).Where(File.Exists).ToList();
            if (candidates.Count == 0)
            {
                missing.Add(recording.File);
                continue;
            }

            if (candidates.Count > 1)
            {
                problems.Add(
                    $"{shownFolder}: для «{recording.File}» два файла — {Relative(folder, candidates[0])} и {Relative(folder, candidates[1])}; оставьте один");
                continue;
            }

            used.Add(candidates[0]);
            var text = await SpeechEvalDataset.ReadTextAsync(candidates[0], cancellationToken).ConfigureAwait(false);
            if (text is null)
            {
                problems.Add($"{shownFolder}/{Relative(folder, candidates[0])} не в UTF-8 — пересохраните его в кодировке UTF-8");
                continue;
            }

            texts.Add(recording.File, text);
        }

        if (problems.Count == 0 && texts.Count == 0)
        {
            problems.Add($"в {shownFolder} нет ни одного файла для записей манифеста (ожидается {shownFolder}/<имя записи>.txt)");
        }

        if (problems.Count > 0)
        {
            throw new SpeechEvalDatasetException(problems);
        }

        var warnings = new List<string>();
        if (missing.Count > 0)
        {
            warnings.Add($"{shownFolder}: нет готового выхода для {Count(missing.Count)} записей — они не оцениваются: {List(missing)}");
        }

        var extra = Directory.EnumerateFiles(folder, "*.txt", SearchOption.AllDirectories)
            .Where(path => !used.Contains(path))
            .Select(path => Relative(folder, path))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (extra.Count > 0)
        {
            warnings.Add($"{shownFolder}: файлы без записи в манифесте (не оцениваются; проверьте имена): {List(extra)}");
        }

        return new SpeechEvalExternalHypotheses(model, folder, texts, warnings);
    }

    /// <summary>
    /// Готовый выход как фрагменты: по одному на непустую строку, номера по порядку, таймкоды нулевые (их нет).
    /// </summary>
    /// <param name="text">Содержимое файла готового выхода.</param>
    public static IReadOnlyList<TranscriptSegmentDraft> ToSegments(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        return text.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .Select((line, index) => new TranscriptSegmentDraft(index, 0, 0, line))
            .ToList();
    }

    private static IEnumerable<string> CandidatePaths(string folder, string recordingFile)
    {
        yield return Path.GetFullPath(Path.Combine(folder, Path.ChangeExtension(recordingFile, ".txt")));
        yield return Path.GetFullPath(Path.Combine(folder, recordingFile + ".txt"));
    }

    private static string Relative(string folder, string path) => Path.GetRelativePath(folder, path).Replace('\\', '/');

    private static string List(List<string> items)
    {
        var listed = string.Join(", ", items.Take(MaxListed));
        return items.Count > MaxListed ? $"{listed} и ещё {Count(items.Count - MaxListed)}" : listed;
    }

    private static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);
}
