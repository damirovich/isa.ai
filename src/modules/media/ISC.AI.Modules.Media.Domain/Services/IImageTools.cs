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

    /// <summary>
    /// Кадр, уменьшенный ровно до <paramref name="width"/>×<paramref name="height"/> (JPEG), — плитка ленты кадров
    /// (ADR-0038). Размер плитки задаёт вызывающий по пропорциям кадра, поэтому искажения нет; только масштаб (ТЭ-007).
    /// </summary>
    byte[] ThumbnailJpeg(byte[] imageBytes, int width, int height, int quality = 80);

    /// <summary>
    /// Плитки одного размера (JPEG, <paramref name="tileWidth"/>×<paramref name="tileHeight"/>) — в одну картинку-ряд
    /// слева направо (лента кадров, ADR-0038): ширина результата — число плиток × ширина плитки.
    /// </summary>
    byte[] ComposeStripJpeg(IReadOnlyList<byte[]> tiles, int tileWidth, int tileHeight, int quality = 80);
}
