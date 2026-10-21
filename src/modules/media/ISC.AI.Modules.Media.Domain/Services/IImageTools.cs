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

    /// <summary>
    /// Обработанная копия фотографии (ТФ-МЕД-14, ADR-0050): изображение с поворотом по EXIF (как его показывает
    /// браузер), затем операции <paramref name="edit"/> в порядке <see cref="ImageEdit"/>; результат — PNG без потерь.
    /// Оригинал не меняется (ТЭ-007) — это новая картинка.
    /// </summary>
    /// <exception cref="ArgumentException">Набор недопустим для этого изображения (текст — что именно, <see cref="ImageEdit.Validate"/>).</exception>
    /// <exception cref="InvalidOperationException">Изображение не распознано.</exception>
    byte[] ApplyEdits(byte[] imageBytes, ImageEdit edit);

    /// <summary>
    /// Изображение с нанесёнными пометками для выгрузки (ТФ-МЕД-13, ADR-0050): поворот по EXIF, стрелки, рамки и
    /// подписи их цветом, внизу — полоса с <paramref name="caption"/> (гриф и происхождение); PNG без потерь.
    /// Без подписи (<see langword="null"/>) полосы нет — снимок для фототаблицы, где гриф и выдача — на самом листе
    /// (ADR-0057).
    /// </summary>
    /// <exception cref="InvalidOperationException">Изображение не распознано.</exception>
    byte[] RenderAnnotated(byte[] imageBytes, IReadOnlyList<AnnotationRow> annotations, string? caption);

    /// <summary>
    /// Снимок для листа фототаблицы (ADR-0057): поворот по EXIF (как показывает браузер), затем уменьшение с
    /// сохранением пропорций, чтобы поместиться в <paramref name="maxWidth"/>×<paramref name="maxHeight"/>; меньший
    /// снимок не увеличивается. Только масштаб (ТЭ-007); JPEG.
    /// </summary>
    /// <exception cref="InvalidOperationException">Изображение не распознано.</exception>
    byte[] FitJpeg(byte[] imageBytes, int maxWidth, int maxHeight, int quality = 90);

    /// <summary>
    /// Фототаблица в PDF (ТФ-МЕД-20, ADR-0057): листы A4 по <see cref="PhotoSheetLayout"/>, подписи цветом текста и
    /// подложки, на каждой странице — маркировка грифа сверху и снизу, «Выдал» и водяной знак получателя (ТБ-033,
    /// ТБ-076); свойства файла — заголовок, исполнитель, гриф.
    /// </summary>
    /// <exception cref="InvalidOperationException">Снимок не распознан.</exception>
    byte[] RenderPhotoSheetPdf(PhotoSheetDocument document);
}
