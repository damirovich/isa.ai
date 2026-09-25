namespace ISC.AI.Speech;

/// <summary>
/// Настройки распознавания речи (секция <c>Speech</c>, ADR-0026). Файлы модели — из офлайн-поставки
/// (<c>deploy/offline/export-speech-models.ps1</c>, ТИ-004); пины SHA-256 ОБЯЗАТЕЛЬНЫ: отсутствие или
/// несовпадение — явная ошибка при запуске расшифровки, а не «речи не нашлось».
/// </summary>
/// <param name="WorkerPath">Путь к процессу-распознавателю (<c>.exe</c> или <c>.dll</c> — тогда через <c>dotnet</c>).</param>
/// <param name="ModelPath">Путь к ONNX модели распознавания.</param>
/// <param name="ModelSha256">Пин SHA-256 модели (hex, без учёта регистра).</param>
/// <param name="TokensPath">Путь к словарю модели.</param>
/// <param name="TokensSha256">Пин SHA-256 словаря.</param>
/// <param name="VadPath">Путь к ONNX детектора речи.</param>
/// <param name="VadSha256">Пин SHA-256 детектора речи.</param>
/// <param name="FfmpegFolder">Каталог ffmpeg/ffprobe; пусто — искать в PATH.</param>
/// <param name="Threads">Потоки модели распознавания.</param>
/// <param name="MaxSegmentSeconds">Наибольшая длина куска звука для модели, секунды.</param>
/// <param name="ModelName">Имя модели в <see cref="ModelVersion"/>.</param>
/// <param name="MaxDurationHours">Наибольшая длительность записи, часы (см. <see cref="MaxDuration"/>).</param>
/// <param name="TimeoutFactor">Множитель таймаута прогона к длительности записи (см. <see cref="EffectiveTimeoutFactor"/>).</param>
public sealed record SpeechOptions(
    string WorkerPath,
    string ModelPath,
    string ModelSha256,
    string TokensPath,
    string TokensSha256,
    string VadPath,
    string VadSha256,
    string? FfmpegFolder = null,
    int Threads = SpeechOptions.DefaultThreads,
    double MaxSegmentSeconds = SpeechOptions.DefaultMaxSegmentSeconds,
    string ModelName = SpeechOptions.DefaultModelName,
    double MaxDurationHours = SpeechOptions.DefaultMaxDurationHours,
    double TimeoutFactor = SpeechOptions.DefaultTimeoutFactor)
{
    /// <summary>Имя модели по умолчанию (ADR-0026).</summary>
    public const string DefaultModelName = "gigaam-multilingual-ctc";

    /// <summary>Потоки по умолчанию: распознавание — фоновая задача и не должно забирать весь процессор у хоста.</summary>
    public const int DefaultThreads = 4;

    /// <summary>Длина куска по умолчанию: GigaAM берёт до ~25 с звука за раз — 20 с с запасом.</summary>
    public const double DefaultMaxSegmentSeconds = 20;

    /// <summary>Наибольшая допустимая длина куска, секунды (предел модели).</summary>
    public const double MaxAllowedSegmentSeconds = 25;

    /// <summary>Наименьшая допустимая длина куска, секунды.</summary>
    public const double MinAllowedSegmentSeconds = 2;

    /// <summary>Сколько первых символов пина модели входит в <see cref="ModelVersion"/>.</summary>
    public const int ModelVersionHashLength = 12;

    /// <summary>
    /// Предел длительности записи по умолчанию, часы: сутки — с запасом до жёсткого предела
    /// <see cref="MaxAllowedDurationHours"/> и не больше суток работы единственной фоновой очереди.
    /// </summary>
    public const double DefaultMaxDurationHours = 24;

    /// <summary>Наименьший допустимый предел длительности записи, часы.</summary>
    public const double MinAllowedDurationHours = 1;

    /// <summary>
    /// Наибольший допустимый предел длительности записи, часы. ЖЁСТКИЙ предел, не настройка вкуса:
    /// индекс отсчёта в детекторе речи sherpa-onnx — int32, и на 2^31 отсчётах (≈37,28 ч при 16 кГц)
    /// он переполняется — прогон падает в самом конце многочасовой работы; тот же порог у WAV (4 ГиБ
    /// данных RIFF при 2 байтах на отсчёт). 36 ч — с запасом до обоих.
    /// </summary>
    public const double MaxAllowedDurationHours = 36;

    /// <summary>
    /// Множитель таймаута по умолчанию: прогон может длиться 15 мин плюс втрое дольше самой записи.
    /// Модель на процессоре распознаёт в разы быстрее реального времени, так что это запас на медленный
    /// или занятый сервер, а не ожидаемая длительность.
    /// </summary>
    public const double DefaultTimeoutFactor = 3;

    /// <summary>Наименьший допустимый множитель таймаута.</summary>
    public const double MinAllowedTimeoutFactor = 0.5;

    /// <summary>
    /// Наибольший допустимый множитель таймаута: при пределе записи 36 ч таймаут остаётся в границах
    /// таймера (<see cref="CancellationTokenSource.CancelAfter(TimeSpan)"/>) и «вечного» прогона не бывает.
    /// </summary>
    public const double MaxAllowedTimeoutFactor = 20;

    /// <summary>
    /// Действующий предел длительности записи: <see cref="MaxDurationHours"/> в границах
    /// [<see cref="MinAllowedDurationHours"/>; <see cref="MaxAllowedDurationHours"/>] (нечисло — умолчание).
    /// Запись длиннее — отказ до запуска ffmpeg и распознавателя (ADR-0026).
    /// </summary>
    public TimeSpan MaxDuration => TimeSpan.FromHours(
        InRange(MaxDurationHours, DefaultMaxDurationHours, MinAllowedDurationHours, MaxAllowedDurationHours));

    /// <summary>
    /// Действующий множитель таймаута: <see cref="TimeoutFactor"/> в границах
    /// [<see cref="MinAllowedTimeoutFactor"/>; <see cref="MaxAllowedTimeoutFactor"/>] (нечисло — умолчание).
    /// </summary>
    public double EffectiveTimeoutFactor =>
        InRange(TimeoutFactor, DefaultTimeoutFactor, MinAllowedTimeoutFactor, MaxAllowedTimeoutFactor);

    /// <summary>
    /// Версия модели для расшифровки и журнала: <c>имя@первые 12 символов SHA-256 модели</c>, например
    /// <c>gigaam-multilingual-ctc@2d94f93ffd4e</c>. Имя одно для вариантов 220M и 600M — различает их хеш:
    /// результаты разных файлов модели не сравнимы, и по версии это должно быть видно.
    /// </summary>
    public string ModelVersion => FormatModelVersion(ModelName, ModelSha256);

    /// <summary>Формирует версию модели (см. <see cref="ModelVersion"/>); пустой пин — <c>имя@unpinned</c>.</summary>
    public static string FormatModelVersion(string? modelName, string? modelSha256)
    {
        var name = string.IsNullOrWhiteSpace(modelName) ? DefaultModelName : modelName.Trim();
        var sha = modelSha256?.Trim() ?? string.Empty;
        if (sha.Length == 0)
        {
            return $"{name}@unpinned";
        }

        return $"{name}@{sha[..Math.Min(ModelVersionHashLength, sha.Length)].ToLowerInvariant()}";
    }

    /// <summary>
    /// Обязательные ключи, которые не заданы (пусто — распознавание настроено). По этому списку
    /// регистрация выбирает между рабочей реализацией и явным отказом «не настроена».
    /// </summary>
    public IReadOnlyList<string> MissingKeys()
    {
        var missing = new List<string>();
        Check(WorkerPath, SpeechConfigurationKeys.WorkerPath);
        Check(ModelPath, SpeechConfigurationKeys.ModelPath);
        Check(ModelSha256, SpeechConfigurationKeys.ModelSha256);
        Check(TokensPath, SpeechConfigurationKeys.TokensPath);
        Check(TokensSha256, SpeechConfigurationKeys.TokensSha256);
        Check(VadPath, SpeechConfigurationKeys.VadPath);
        Check(VadSha256, SpeechConfigurationKeys.VadSha256);
        return missing;

        void Check(string? value, string key)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                missing.Add(key);
            }
        }
    }

    // Записи создаются и напрямую (тесты, eval), минуя ReadOptions, — границы соблюдаются и здесь.
    private static double InRange(double value, double fallback, double min, double max) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}
