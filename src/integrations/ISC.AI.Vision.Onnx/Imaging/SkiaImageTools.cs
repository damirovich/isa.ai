using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using SkiaSharp;

namespace ISC.AI.Vision.Onnx.Imaging;

/// <summary>
/// Утилиты над изображениями на SkiaSharp (MIT): размер кадра и вырезка лица в JPEG для показа в выдаче
/// (ТФ-ПЛ-02). Только кадрирование и масштабирование — без шумоподавления, резкости, коррекции цвета и
/// иных «улучшений» (ТЭ-007): показываемая вырезка должна отражать исходный материал, а не обработку.
/// </summary>
public sealed class SkiaImageTools : IImageTools
{
    /// <inheritdoc />
    public ImageSize ReadSize(byte[] imageBytes)
    {
        ArgumentNullException.ThrowIfNull(imageBytes);

        // Кодек читает заголовок без декодирования растра (для JPEG/PNG/WebP/BMP/GIF).
        using var data = SKData.CreateCopy(imageBytes);
        using var codec = SKCodec.Create(data);
        if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0)
        {
            throw new InvalidOperationException("Изображение не распознано: формат не поддерживается или файл повреждён.");
        }

        return new ImageSize(codec.Info.Width, codec.Info.Height);
    }

    /// <inheritdoc />
    public byte[] CropJpeg(byte[] imageBytes, BoundingBox box, float marginRatio = 0.25f, int maxSide = 256, int quality = 85)
    {
        ArgumentNullException.ThrowIfNull(imageBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(marginRatio);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxSide, 1);
        var jpegQuality = Math.Clamp(quality, 1, 100);

        using var source = ImageDecoder.DecodeRgba(imageBytes);

        // Рамка с полями, обрезанная по границам изображения; вырожденная рамка — ошибка входа, не пустой JPEG.
        var marginX = box.Width * marginRatio;
        var marginY = box.Height * marginRatio;
        var left = Math.Clamp((int)MathF.Floor(box.X - marginX), 0, source.Width);
        var top = Math.Clamp((int)MathF.Floor(box.Y - marginY), 0, source.Height);
        var right = Math.Clamp((int)MathF.Ceiling(box.Right + marginX), 0, source.Width);
        var bottom = Math.Clamp((int)MathF.Ceiling(box.Bottom + marginY), 0, source.Height);
        var cropWidth = right - left;
        var cropHeight = bottom - top;
        if (cropWidth <= 0 || cropHeight <= 0)
        {
            throw new ArgumentException("Рамка лица не пересекается с изображением — вырезка невозможна.", nameof(box));
        }

        // Масштаб — только вниз, по большей стороне; маленькую вырезку не растягиваем (ТЭ-007).
        var scale = Math.Min(1f, (float)maxSide / Math.Max(cropWidth, cropHeight));
        var targetWidth = Math.Max(1, (int)MathF.Round(cropWidth * scale));
        var targetHeight = Math.Max(1, (int)MathF.Round(cropHeight * scale));

        using var target = new SKBitmap(new SKImageInfo(targetWidth, targetHeight, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(target))
        using (var image = SKImage.FromBitmap(source))
        {
            canvas.Clear(SKColors.Black);
            canvas.DrawImage(
                image,
                new SKRect(left, top, right, bottom),
                new SKRect(0, 0, targetWidth, targetHeight),
                new SKSamplingOptions(SKCubicResampler.Mitchell));
        }

        using var encoded = target.Encode(SKEncodedImageFormat.Jpeg, jpegQuality)
            ?? throw new InvalidOperationException("Не удалось закодировать вырезку в JPEG.");
        return encoded.ToArray();
    }
}
