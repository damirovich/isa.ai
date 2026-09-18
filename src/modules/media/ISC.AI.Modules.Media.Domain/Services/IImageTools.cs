using ISC.AI.Modules.Media.Domain.Model;

namespace ISC.AI.Modules.Media.Domain.Services;

/// <summary>Размер изображения в пикселях.</summary>
public readonly record struct ImageSize(int Width, int Height);

/// <summary>
/// Утилиты над изображениями для конвейера индексации (реализация — интеграция с растровой библиотекой):
/// размер кадра для оценки качества и вырезка лица в JPEG для показа в выдаче (ТФ-ПЛ-02). Без «улучшений»
/// изображения (ТЭ-007): только кадрирование и масштабирование.
/// </summary>
public interface IImageTools
{
    /// <summary>Размер изображения без полного декодирования растра, если формат позволяет.</summary>
    ImageSize ReadSize(byte[] imageBytes);

    /// <summary>Вырезка лица с полями (<paramref name="marginRatio"/> от размера рамки), ужатая до <paramref name="maxSide"/> по большей стороне.</summary>
    byte[] CropJpeg(byte[] imageBytes, BoundingBox box, float marginRatio = 0.25f, int maxSide = 256, int quality = 85);
}
