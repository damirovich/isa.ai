using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ISC.AI.Evals.Speech;

/// <summary>Запись эталонного набора: аудио- или видеофайл, его эталонная расшифровка и разметка.</summary>
/// <param name="File">Путь к записи относительно папки набора — как в манифесте; ключ записи в отчёте.</param>
/// <param name="AudioPath">Полный путь к файлу записи.</param>
/// <param name="ReferencePath">Полный путь к эталону <c>&lt;имя&gt;.txt</c> рядом с записью.</param>
/// <param name="Language">
/// Язык речи; <see langword="null"/> — контрольный файл без речи (в манифесте язык «-», эталон пуст): в WER/CER не
/// входит, по нему считаются ложные слова и фрагменты.
/// </param>
/// <param name="Condition">Условия записи.</param>
/// <param name="Note">Примечание из манифеста.</param>
/// <param name="ReferenceText">
/// Текст эталона как есть (нормализуется при расчёте); пометки <c>[неразборчиво]</c> — участки без оценки.
/// </param>
public sealed record SpeechEvalRecording(
    string File,
    string AudioPath,
    string ReferencePath,
    SpeechLanguage? Language,
    RecordingCondition Condition,
    string Note,
    string ReferenceText)
{
    /// <summary>Контрольный файл без речи (методика пилота, 3.4 и 6.6).</summary>
    public bool IsNoSpeech => Language is null;

    /// <summary>Сколько в эталоне пометок <c>[неразборчиво]</c> — для отчёта.</summary>
    public int UnintelligibleRegions => SpeechTextNormalizer.CountUnintelligible(ReferenceText);

    /// <summary>
    /// Запись «только для скорости» (методика пилота, 7.2): речевая, но в эталоне нет ни одного оцениваемого
    /// слова — по методике это длинная запись целиком с эталоном из одной пометки <c>[неразборчиво]</c>. В сводки
    /// качества (записей, WER/CER, «Не оцен.») она не входит: её слова модели там исказили бы картину разметки.
    /// Время, длительность и RTF таких записей показываются отдельно
    /// (<see cref="SpeechEvalAggregator.SpeedOnly"/>).
    /// </summary>
    public bool IsSpeedOnly => !IsNoSpeech && SpeechTextNormalizer.CountScoredReferenceWords(ReferenceText) == 0;
}

/// <summary>
/// Эталонный набор для оценки расшифровки (КИ-10, ADR-0026): папка с записями, эталонами
/// <c>&lt;имя&gt;.txt</c> рядом с каждой записью и манифестом <see cref="SpeechEvalManifest.FileName"/>.
/// </summary>
/// <remarks>
/// <para>РЕЖИМ. Набор пилота — реальные записи заказчика и их расшифровки, т. е. материалы дел: папка набора и
/// папка отчёта живут только в контуре, НЕ в репозитории и НЕ в тестах (как фото людей, ТО-прог-13).
/// Тесты этого проекта работают на синтетических файлах.</para>
/// <para>Подпапка <see cref="SpeechEvalExternalHypotheses.FolderName"/> зарезервирована под готовый выход
/// сторонних моделей (<c>hyp/&lt;модель&gt;/&lt;имя&gt;.txt</c>): записи в ней не допускаются, а её <c>.txt</c>
/// не считаются забытыми эталонами.</para>
/// </remarks>
/// <param name="rootFolder">Полный путь к папке набора.</param>
/// <param name="recordings">Записи в порядке манифеста.</param>
/// <param name="warnings">Замечания к набору (эталоны, лишние файлы) — попадают в отчёт.</param>
public sealed class SpeechEvalDataset(
    string rootFolder, IReadOnlyList<SpeechEvalRecording> recordings, IReadOnlyList<string> warnings)
{
    private const int MaxListedUnreferenced = 20;

    // Строгий UTF-8: эталон, сохранённый Блокнотом «в ANSI» (Windows-1251), иначе прочитался бы кракозябрами
    // и дал бы WER около 100 % без единой ошибки модели.
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Полный путь к папке набора.</summary>
    public string RootFolder { get; } = rootFolder ?? throw new ArgumentNullException(nameof(rootFolder));

    /// <summary>Записи в порядке манифеста.</summary>
    public IReadOnlyList<SpeechEvalRecording> Recordings { get; } = recordings ?? throw new ArgumentNullException(nameof(recordings));

    /// <summary>Замечания к набору: не мешают прогону, но могут исказить метрики — выводятся в отчёт.</summary>
    public IReadOnlyList<string> Warnings { get; } = warnings ?? throw new ArgumentNullException(nameof(warnings));

    /// <summary>
    /// Загружает набор из папки: разбирает манифест, проверяет наличие записей и эталонов, читает эталоны
    /// (строго UTF-8 или UTF-16 с BOM) и собирает замечания <see cref="ReferenceTextLint"/>.
    /// </summary>
    /// <param name="folder">Папка набора.</param>
    /// <param name="cancellationToken">Отмена.</param>
    /// <exception cref="SpeechEvalDatasetException">Набор непригоден: перечислены ВСЕ найденные проблемы.</exception>
    public static async Task<SpeechEvalDataset> LoadAsync(string folder, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        var root = Path.GetFullPath(folder);
        if (!Directory.Exists(root))
        {
            throw new SpeechEvalDatasetException([$"папка набора не найдена: {root}"]);
        }

        var manifestPath = Path.Combine(root, SpeechEvalManifest.FileName);
        if (!File.Exists(manifestPath))
        {
            throw new SpeechEvalDatasetException(
                [$"нет манифеста {SpeechEvalManifest.FileName} в папке {root} (формат: файл;язык;условия;примечание)"]);
        }

        var manifestText = await ReadTextAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        if (manifestText is null)
        {
            throw new SpeechEvalDatasetException(
                [$"{SpeechEvalManifest.FileName} не в UTF-8 — сохраните его как «CSV UTF-8 (разделитель — точка с запятой)»"]);
        }

        var parsed = SpeechEvalManifest.Parse(SplitLines(manifestText));
        var problems = new List<string>(parsed.Errors);
        var warnings = new List<string>();
        var recordings = new List<SpeechEvalRecording>();
        var references = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var rootPrefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        var hypothesesPrefix = Path.Combine(root, SpeechEvalExternalHypotheses.FolderName) + Path.DirectorySeparatorChar;

        foreach (var entry in parsed.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var location = $"{SpeechEvalManifest.FileName}, строка {entry.LineNumber.ToString(CultureInfo.InvariantCulture)}";
            var audioPath = Path.IsPathRooted(entry.File) ? null : Path.GetFullPath(Path.Combine(root, entry.File));
            if (audioPath is null || !audioPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"{location}: путь «{entry.File}» должен быть относительным и не выходить за папку набора");
                continue;
            }

            if (audioPath.StartsWith(hypothesesPrefix, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add(
                    $"{location}: «{entry.File}» лежит в папке {SpeechEvalExternalHypotheses.FolderName} — она зарезервирована под готовый выход сторонних моделей");
                continue;
            }

            if (string.Equals(Path.GetExtension(audioPath), ".txt", StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"{location}: «{entry.File}» — это .txt; в манифесте указывается файл записи, эталон ищется рядом с ним");
                continue;
            }

            if (!File.Exists(audioPath))
            {
                problems.Add($"{location}: нет файла записи {audioPath}");
                continue;
            }

            var referencePath = Path.ChangeExtension(audioPath, ".txt");
            if (references.TryGetValue(referencePath, out var otherFile))
            {
                problems.Add($"{location}: «{entry.File}» и «{otherFile}» указывают на один эталон {Path.GetFileName(referencePath)} — переименуйте одну из записей");
                continue;
            }

            references.Add(referencePath, entry.File);
            var noSpeech = entry.Language is null;
            if (!File.Exists(referencePath))
            {
                if (!noSpeech)
                {
                    problems.Add($"{location}: нет эталона {referencePath} (рядом с записью, то же имя, расширение .txt)");
                    continue;
                }

                // Контрольный файл без речи: эталон можно не заводить — он пуст по определению.
                recordings.Add(new SpeechEvalRecording(
                    entry.File, audioPath, referencePath, Language: null, entry.Condition, entry.Note, ReferenceText: string.Empty));
                continue;
            }

            var referenceText = await ReadTextAsync(referencePath, cancellationToken).ConfigureAwait(false);
            if (referenceText is null)
            {
                problems.Add($"{location}: эталон {Path.GetFileName(referencePath)} не в UTF-8 — пересохраните его в кодировке UTF-8");
                continue;
            }

            // Пустой эталон и язык «-» должны совпадать: иначе забытый (не заполненный) эталон речевой записи молча
            // стал бы «контрольным файлом», а его пропуски — «ложными словами» (методика пилота, 3.4).
            var referenceIsEmpty = SpeechTextNormalizer.ReferenceWords(referenceText).Count == 0;
            if (noSpeech && !referenceIsEmpty)
            {
                problems.Add(
                    $"{location}: язык «{SpeechEvalCodes.NoSpeechCode}» — контрольный файл без речи, а эталон {Path.GetFileName(referencePath)} не пуст — "
                    + "укажите язык записи (ru, ky, mixed) или очистите эталон");
                continue;
            }

            if (!noSpeech && referenceIsEmpty)
            {
                problems.Add(
                    $"{location}: эталон {Path.GetFileName(referencePath)} пуст — заполните его; если это контрольный файл без речи, "
                    + $"укажите в манифесте язык «{SpeechEvalCodes.NoSpeechCode}»");
                continue;
            }

            if (!noSpeech)
            {
                warnings.AddRange(ReferenceTextLint.Check(referenceText).Select(warning => $"{entry.File}: {warning}"));
            }

            recordings.Add(new SpeechEvalRecording(
                entry.File, audioPath, referencePath, entry.Language, entry.Condition, entry.Note, referenceText));
        }

        if (parsed.Entries.Count == 0 && parsed.Errors.Count == 0)
        {
            problems.Add($"в {SpeechEvalManifest.FileName} нет ни одной записи");
        }

        if (problems.Count > 0)
        {
            throw new SpeechEvalDatasetException(problems);
        }

        var unreferenced = Directory.EnumerateFiles(root, "*.txt", SearchOption.AllDirectories)
            .Where(path => !references.ContainsKey(path) && !path.StartsWith(hypothesesPrefix, StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.GetRelativePath(root, path))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (unreferenced.Count > 0)
        {
            var listed = string.Join(", ", unreferenced.Take(MaxListedUnreferenced));
            var more = unreferenced.Count > MaxListedUnreferenced ? $" и ещё {(unreferenced.Count - MaxListedUnreferenced).ToString(CultureInfo.InvariantCulture)}" : string.Empty;
            warnings.Add($"эталоны без строки в манифесте (не оцениваются): {listed}{more}");
        }

        return new SpeechEvalDataset(root, recordings, warnings);
    }

    /// <summary>
    /// Читает текстовый файл набора: UTF-8 (с BOM или без) строго, UTF-16 — только с BOM. Возвращает
    /// <see langword="null"/>, если байты не являются корректным UTF-8 (файл в Windows-1251 и т. п.).
    /// </summary>
    /// <param name="path">Путь к файлу.</param>
    /// <param name="cancellationToken">Отмена.</param>
    public static async Task<string?> ReadTextAsync(string path, CancellationToken cancellationToken = default)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return DecodeText(bytes);
    }

    /// <summary>Раскодирует байты по правилам <see cref="ReadTextAsync"/>.</summary>
    /// <param name="bytes">Содержимое файла.</param>
    public static string? DecodeText(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
        {
            return Encoding.Unicode.GetString(bytes[2..]);
        }

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
        {
            return Encoding.BigEndianUnicode.GetString(bytes[2..]);
        }

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            bytes = bytes[3..];
        }

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    private static IEnumerable<string> SplitLines(string text) =>
        text.Split('\n').Select(line => line.TrimEnd('\r'));
}

/// <summary>Набор непригоден к прогону; <see cref="Problems"/> перечисляет все найденные проблемы.</summary>
public sealed class SpeechEvalDatasetException : Exception
{
    /// <summary>Создаёт исключение без описания проблем.</summary>
    public SpeechEvalDatasetException()
        : this("Эталонный набор непригоден.")
    {
    }

    /// <summary>Создаёт исключение с сообщением.</summary>
    /// <param name="message">Сообщение.</param>
    public SpeechEvalDatasetException(string message)
        : base(message)
    {
        Problems = [message];
    }

    /// <summary>Создаёт исключение с сообщением и причиной.</summary>
    /// <param name="message">Сообщение.</param>
    /// <param name="innerException">Причина.</param>
    public SpeechEvalDatasetException(string message, Exception innerException)
        : base(message, innerException)
    {
        Problems = [message];
    }

    /// <summary>Создаёт исключение по списку проблем набора.</summary>
    /// <param name="problems">Проблемы (каждая — отдельной строкой сообщения).</param>
    public SpeechEvalDatasetException(IReadOnlyList<string> problems)
        : base(Describe(problems))
    {
        Problems = problems;
    }

    /// <summary>Все найденные проблемы набора.</summary>
    public IReadOnlyList<string> Problems { get; }

    private static string Describe(IReadOnlyList<string> problems)
    {
        ArgumentNullException.ThrowIfNull(problems);
        return "Эталонный набор непригоден:" + Environment.NewLine
            + string.Join(Environment.NewLine, problems.Select(problem => "  - " + problem));
    }
}
