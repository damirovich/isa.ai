namespace ISC.AI.Vision.Onnx;

/// <summary>
/// Настройки распознавания (секция <c>Vision</c> конфигурации). Файлы моделей — из дистрибутива
/// (<c>deploy/offline/models</c>, ТИ-004); пины SHA-256 ОБЯЗАТЕЛЬНЫ: отсутствие или несовпадение —
/// явная ошибка при первом обращении, а не «тихо не нашлось» (fail-closed по образцу OCR).
/// </summary>
/// <param name="DetectorModelPath">Путь к ONNX детектора (YuNet).</param>
/// <param name="DetectorSha256">Пин SHA-256 детектора (hex, без учёта регистра).</param>
/// <param name="EmbedderModelPath">Путь к ONNX векторизатора (SFace).</param>
/// <param name="EmbedderSha256">Пин SHA-256 векторизатора.</param>
/// <param name="FfmpegFolder">Каталог с бинарниками ffmpeg/ffprobe; пусто — искать в PATH.</param>
/// <param name="DetectionScoreThreshold">Порог уверенности детектора (дефолт OpenCV — 0.9).</param>
/// <param name="NmsIouThreshold">Порог IoU подавления дублей (дефолт OpenCV — 0.3).</param>
/// <param name="MaxInputSide">Большая сторона входа детектора; крупнее — уменьшается с сохранением пропорций.</param>
/// <param name="MinInterocularDistance">Минимальное межзрачковое расстояние (пикс.) для пригодного лица (ТО-мат-07).</param>
/// <param name="MinDetectionScoreForQuality">Минимальная уверенность детектора для пригодного лица.</param>
public sealed record VisionOptions(
    string DetectorModelPath,
    string DetectorSha256,
    string EmbedderModelPath,
    string EmbedderSha256,
    string? FfmpegFolder = null,
    float DetectionScoreThreshold = 0.9f,
    float NmsIouThreshold = 0.3f,
    int MaxInputSide = 640,
    float MinInterocularDistance = 20f,
    float MinDetectionScoreForQuality = 0.9f);
