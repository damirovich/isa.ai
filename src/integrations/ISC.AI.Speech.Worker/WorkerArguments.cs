using System.Globalization;

namespace ISC.AI.Speech.Worker;

/// <summary>
/// Аргументы процесса-распознавателя (ADR-0026). Разбор — чистая функция <see cref="Parse"/> без обращения
/// к диску; существование файлов проверяет <see cref="FindMissingFiles"/> отдельно, чтобы разбор можно
/// было проверить тестом без файлов.
/// </summary>
/// <param name="ModelPath">ONNX модели распознавания (NeMo CTC; GigaAM Multilingual).</param>
/// <param name="TokensPath">Словарь модели <c>tokens.txt</c>.</param>
/// <param name="VadPath">ONNX детектора речи Silero VAD.</param>
/// <param name="InputPath">Вход: WAV 16 кГц, моно, 16 бит (готовит адаптер через ffmpeg).</param>
/// <param name="Threads">Потоки модели распознавания.</param>
/// <param name="MaxSegmentSeconds">Наибольшая длина куска, подаваемого модели, секунды.</param>
/// <param name="FeatureDim">Число мел-полос признаков (GigaAM — 64; метаданные модели имеют приоритет).</param>
/// <param name="DecodeWhole">
/// Диагностический режим <c>--decode-whole</c>: вся запись без детектора речи подаётся модели кусками не длиннее
/// <paramref name="MaxSegmentSeconds"/>. Нужен, чтобы проверить путь декодирования (признаки, модель, словарь) на
/// синтетическом звуке без записей людей: тишину и тон детектор речи до модели не пропускает. Адаптер этот
/// ключ не передаёт.
/// </param>
internal sealed record WorkerArguments(
    string ModelPath,
    string TokensPath,
    string VadPath,
    string InputPath,
    int Threads = WorkerArguments.DefaultThreads,
    double MaxSegmentSeconds = WorkerArguments.DefaultMaxSegmentSeconds,
    int FeatureDim = WorkerArguments.DefaultFeatureDim,
    bool DecodeWhole = false)
{
    /// <summary>Ключ диагностического режима (переключатель без значения); на это имя опираются интеграционные тесты.</summary>
    public const string DecodeWholeKey = "--decode-whole";

    /// <summary>Потоки по умолчанию.</summary>
    public const int DefaultThreads = 4;

    /// <summary>
    /// Длина куска по умолчанию. GigaAM обучена на фразах до ~25–30 с: длиннее — растёт память
    /// внимания и падает качество, поэтому 20 с с запасом.
    /// </summary>
    public const double DefaultMaxSegmentSeconds = 20;

    /// <summary>Верхняя граница длины куска: дальше модель выходит за длины, на которых обучалась.</summary>
    public const double MaxAllowedSegmentSeconds = 30;

    /// <summary>Нижняя граница длины куска: короче — слова режутся чаще, чем кончаются фразы.</summary>
    public const double MinAllowedSegmentSeconds = 2;

    /// <summary>Мел-полосы GigaAM (карточка модели: 64 полосы, n_fft 320, шаг 160).</summary>
    public const int DefaultFeatureDim = 64;

    /// <summary>Строка помощи — печатается в stderr при ошибке аргументов.</summary>
    public const string Usage =
        "ISC.AI.Speech.Worker --model <model.onnx> --tokens <tokens.txt> --vad <silero_vad.onnx> --input <wav 16 кГц моно>"
        + " [--threads N] [--max-segment-seconds 20] [--feature-dim 64] [" + DecodeWholeKey + "]\n"
        + "  " + DecodeWholeKey + " — диагностика: вся запись без детектора речи кусками до --max-segment-seconds"
        + " (проверка декодирования на синтетическом звуке; в работе не используется).";

    /// <summary>
    /// Разбирает аргументы командной строки. Неизвестный ключ, повтор ключа, ключ без значения, нечисловое
    /// или вне допустимых границ значение, отсутствие обязательного ключа — <see cref="WorkerUsageException"/>
    /// с понятным текстом (молча подставленное значение по умолчанию вместо опечатки скрыло бы ошибку настройки).
    /// </summary>
    /// <exception cref="WorkerUsageException">Аргументы неверны.</exception>
    public static WorkerArguments Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var decodeWhole = false;
        for (var i = 0; i < args.Count; i++)
        {
            var key = args[i];

            // Переключатель без значения: следующий аргумент — уже другой ключ.
            if (key == DecodeWholeKey)
            {
                if (decodeWhole)
                {
                    throw new WorkerUsageException($"Аргумент {key} задан дважды.");
                }

                decodeWhole = true;
                continue;
            }

            if (key is not ("--model" or "--tokens" or "--vad" or "--input" or "--threads" or "--max-segment-seconds" or "--feature-dim"))
            {
                throw new WorkerUsageException($"Неизвестный аргумент «{key}».");
            }

            if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new WorkerUsageException($"Для аргумента {key} не задано значение.");
            }

            if (!values.TryAdd(key, args[++i]))
            {
                throw new WorkerUsageException($"Аргумент {key} задан дважды.");
            }
        }

        return new WorkerArguments(
            ModelPath: Required(values, "--model"),
            TokensPath: Required(values, "--tokens"),
            VadPath: Required(values, "--vad"),
            InputPath: Required(values, "--input"),
            Threads: values.TryGetValue("--threads", out var threads) ? ParseInt(threads, "--threads", 1, 64) : DefaultThreads,
            MaxSegmentSeconds: values.TryGetValue("--max-segment-seconds", out var max)
                ? ParseDouble(max, "--max-segment-seconds", MinAllowedSegmentSeconds, MaxAllowedSegmentSeconds)
                : DefaultMaxSegmentSeconds,
            FeatureDim: values.TryGetValue("--feature-dim", out var dim) ? ParseInt(dim, "--feature-dim", 1, 512) : DefaultFeatureDim,
            DecodeWhole: decodeWhole);
    }

    /// <summary>Возвращает описания отсутствующих файлов (пусто — все на месте). Пути — полные.</summary>
    public IReadOnlyList<string> FindMissingFiles(Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(fileExists);

        var missing = new List<string>();
        foreach (var (key, path) in new[] { ("--model", ModelPath), ("--tokens", TokensPath), ("--vad", VadPath), ("--input", InputPath) })
        {
            if (!fileExists(path))
            {
                missing.Add($"{key}: файл не найден — {Path.GetFullPath(path)}");
            }
        }

        return missing;
    }

    private static string Required(Dictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new WorkerUsageException($"Не задан обязательный аргумент {key}.");

    private static int ParseInt(string raw, string key, int min, int max) =>
        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= min && value <= max
            ? value
            : throw new WorkerUsageException($"Аргумент {key} = «{raw}»: ожидается целое от {min} до {max}.");

    private static double ParseDouble(string raw, string key, double min, double max) =>
        double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value >= min && value <= max
            ? value
            : throw new WorkerUsageException(
                $"Аргумент {key} = «{raw}»: ожидается число от {min.ToString(CultureInfo.InvariantCulture)} до {max.ToString(CultureInfo.InvariantCulture)} (разделитель — точка).");
}

/// <summary>Ошибка аргументов командной строки — процесс завершается кодом <see cref="WorkerExitCodes.InvalidArguments"/>.</summary>
internal sealed class WorkerUsageException(string message) : Exception(message);
