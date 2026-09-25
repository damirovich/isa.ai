namespace ISC.AI.Speech;

/// <summary>
/// Ключи конфигурации распознавания речи (секция <c>Speech</c>, ADR-0026). Список <see cref="All"/> —
/// для манифеста модуля (перечень ключей пакета, как <c>MediaModule.ConfigurationKeys</c>).
/// </summary>
/// <remarks>
/// Обязательны пути и пины SHA-256 трёх файлов модели и путь к процессу-распознавателю: без любого из них
/// расшифровка ЯВНО отказывает («не настроена»), а не пропускается молча (ТИ-004, fail-closed как у OCR).
/// Остальные — с умолчаниями.
/// </remarks>
public static class SpeechConfigurationKeys
{
    /// <summary>Путь к процессу-распознавателю <c>ISC.AI.Speech.Worker(.exe|.dll)</c>.</summary>
    public const string WorkerPath = "Speech:Worker:Path";

    /// <summary>Путь к ONNX модели распознавания (GigaAM Multilingual CTC, int8).</summary>
    public const string ModelPath = "Speech:Model:Path";

    /// <summary>Пин SHA-256 модели распознавания.</summary>
    public const string ModelSha256 = "Speech:Model:Sha256";

    /// <summary>Необязательное имя модели для <c>ModelVersion</c> (по умолчанию <c>gigaam-multilingual-ctc</c>).</summary>
    public const string ModelName = "Speech:Model:Name";

    /// <summary>Путь к словарю модели <c>tokens.txt</c>.</summary>
    public const string TokensPath = "Speech:Tokens:Path";

    /// <summary>Пин SHA-256 словаря.</summary>
    public const string TokensSha256 = "Speech:Tokens:Sha256";

    /// <summary>Путь к ONNX детектора речи Silero VAD.</summary>
    public const string VadPath = "Speech:Vad:Path";

    /// <summary>Пин SHA-256 детектора речи.</summary>
    public const string VadSha256 = "Speech:Vad:Sha256";

    /// <summary>Потоки модели распознавания (по умолчанию 4, от 1 до 64).</summary>
    public const string Threads = "Speech:Threads";

    /// <summary>Наибольшая длина куска звука для модели, секунды (по умолчанию 20, от 2 до 25).</summary>
    public const string MaxSegmentSeconds = "Speech:MaxSegmentSeconds";

    /// <summary>
    /// Наибольшая длительность записи, часы (по умолчанию 24, от 1 до 36). Запись длиннее — явный отказ
    /// «разбейте на части» до запуска ffmpeg и распознавателя: 36 ч — жёсткий предел детектора речи
    /// (индекс отсчёта int32) и WAV (4 ГиБ).
    /// </summary>
    public const string MaxDurationHours = "Speech:MaxDurationHours";

    /// <summary>
    /// Множитель таймаута прогона (по умолчанию 3, от 0,5 до 20): прогон длится не дольше
    /// 15 мин + множитель × длительность записи, иначе процессы останавливаются и расшифровка — ошибка.
    /// </summary>
    public const string TimeoutFactor = "Speech:TimeoutFactor";

    /// <summary>Каталог ffmpeg/ffprobe; пусто — берётся <see cref="VisionFfmpegFolder"/>, пусто и там — PATH.</summary>
    public const string FfmpegFolder = "Speech:Ffmpeg:Folder";

    /// <summary>Каталог ffmpeg распознавания лиц — запасной источник для <see cref="FfmpegFolder"/> (одна поставка ffmpeg на оба конвейера).</summary>
    public const string VisionFfmpegFolder = "Vision:Ffmpeg:Folder";

    /// <summary>Все ключи секции <c>Speech</c>, которые читает интеграция.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        WorkerPath,
        ModelPath, ModelSha256, ModelName,
        TokensPath, TokensSha256,
        VadPath, VadSha256,
        Threads, MaxSegmentSeconds,
        MaxDurationHours, TimeoutFactor,
        FfmpegFolder,
    ];
}
