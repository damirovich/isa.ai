using ISC.AI.Modules.Media.Domain.Model;

namespace ISC.AI.Modules.Media.Domain.Services;

/// <summary>
/// Порт детекции лиц (ТО-мат-05): изображение → лица с рамкой, пятью ключевыми точками и
/// уверенностью. Один и тот же порт используется для носителей, эталонов фигурантов и пробных
/// изображений — симметрия конвейера обязательна, иначе шаблоны несопоставимы.
/// </summary>
/// <remarks>
/// Реализация — интеграция (ONNX, модель YuNet); модель и её хеш задаются конфигурацией;
/// отсутствие или подмена файла модели — явная ошибка при первом обращении (ТИ-004), не «ноль лиц».
/// </remarks>
public interface IFaceDetector
{
    /// <summary>Версия/идентификатор модели детектора — для реестра моделей и аудита (ТФ-АДМ-02).</summary>
    string ModelVersion { get; }

    /// <summary>Находит лица на изображении (JPEG/PNG/BMP/WebP в байтах). Пустой список — лиц нет.</summary>
    Task<IReadOnlyList<DetectedFace>> DetectAsync(byte[] imageBytes, CancellationToken cancellationToken = default);
}
