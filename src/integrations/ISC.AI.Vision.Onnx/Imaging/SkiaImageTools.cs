using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using SkiaSharp;

namespace ISC.AI.Vision.Onnx.Imaging;

/// <summary>
/// Утилиты над изображениями на SkiaSharp (MIT): размер кадра, вырезка лица в JPEG для показа в выдаче
/// (ТФ-ПЛ-02) и плитки ленты кадров видео (ADR-0038). Только кадрирование и масштабирование — без шумоподавления, резкости, коррекции цвета и
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

    /// <inheritdoc />
    /// <remarks>
    /// Тот же декодер, что у детектора и вырезки (<see cref="ImageDecoder.DecodeRgba"/>): рамка — в его координатах.
    /// Область приводится к 96 пикселям по ширине — дисперсия лапласиана сравнима у крупных и мелких лиц; яркость —
    /// Y = 0,299·R + 0,587·G + 0,114·B; лапласиан — четыре соседа.
    /// </remarks>
    public FaceImageStats MeasureFace(byte[] imageBytes, BoundingBox box)
    {
        ArgumentNullException.ThrowIfNull(imageBytes);
        const int TargetWidth = 96;

        using var source = ImageDecoder.DecodeRgba(imageBytes);
        var left = Math.Clamp((int)MathF.Floor(box.X), 0, source.Width);
        var top = Math.Clamp((int)MathF.Floor(box.Y), 0, source.Height);
        var right = Math.Clamp((int)MathF.Ceiling(box.Right), 0, source.Width);
        var bottom = Math.Clamp((int)MathF.Ceiling(box.Bottom), 0, source.Height);
        if (right - left <= 0 || bottom - top <= 0)
        {
            throw new ArgumentException("Рамка лица не пересекается с изображением — измерить нечего.", nameof(box));
        }

        var height = Math.Max(1, (int)MathF.Round((bottom - top) * (TargetWidth / (float)(right - left))));
        using var target = NewCanvasBitmap(TargetWidth, height);
        using (var canvas = new SKCanvas(target))
        using (var image = SKImage.FromBitmap(source))
        {
            canvas.DrawImage(
                image, new SKRect(left, top, right, bottom), new SKRect(0, 0, TargetWidth, height), new SKSamplingOptions(SKFilterMode.Linear));
        }

        var pixels = target.GetPixelSpan();
        var luma = new float[TargetWidth * height];
        double sum = 0, squares = 0;
        for (var i = 0; i < luma.Length; i++)
        {
            var y = (0.299f * pixels[i * 4]) + (0.587f * pixels[(i * 4) + 1]) + (0.114f * pixels[(i * 4) + 2]);
            luma[i] = y;
            sum += y;
            squares += y * y;
        }

        var mean = sum / luma.Length;
        var deviation = Math.Sqrt(Math.Max(0, (squares / luma.Length) - (mean * mean)));

        double laplacianSum = 0, laplacianSquares = 0;
        var count = 0;
        for (var y = 1; y < height - 1; y++)
        {
            for (var x = 1; x < TargetWidth - 1; x++)
            {
                var at = (y * TargetWidth) + x;
                double value = (4 * luma[at]) - luma[at - 1] - luma[at + 1] - luma[at - TargetWidth] - luma[at + TargetWidth];
                laplacianSum += value;
                laplacianSquares += value * value;
                count++;
            }
        }

        var variance = count == 0 ? 0 : (laplacianSquares / count) - Math.Pow(laplacianSum / count, 2);
        return new FaceImageStats((float)Math.Max(0, variance), (float)mean, (float)deviation);
    }

    /// <inheritdoc />
    public byte[] ThumbnailJpeg(byte[] imageBytes, int width, int height, int quality = 80)
    {
        ArgumentNullException.ThrowIfNull(imageBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);

        using var source = ImageDecoder.DecodeRgba(imageBytes);
        using var target = NewCanvasBitmap(width, height);
        using (var canvas = new SKCanvas(target))
        using (var image = SKImage.FromBitmap(source))
        {
            canvas.Clear(SKColors.Black);
            canvas.DrawImage(
                image,
                new SKRect(0, 0, source.Width, source.Height),
                new SKRect(0, 0, width, height),
                new SKSamplingOptions(SKCubicResampler.Mitchell));
        }

        return EncodeJpeg(target, quality, "плитку ленты кадров");
    }

    /// <inheritdoc />
    public byte[] ComposeStripJpeg(IReadOnlyList<byte[]> tiles, int tileWidth, int tileHeight, int quality = 80)
    {
        ArgumentNullException.ThrowIfNull(tiles);
        ArgumentOutOfRangeException.ThrowIfLessThan(tileWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(tileHeight, 1);
        if (tiles.Count == 0)
        {
            throw new ArgumentException("Лента кадров без кадров не собирается.", nameof(tiles));
        }

        // Ширина JPEG ограничена 65 535 пикселями — ленту длиннее собрать нельзя (сборщик ограничивает число кадров).
        var width = (long)tiles.Count * tileWidth;
        if (width > ushort.MaxValue)
        {
            throw new ArgumentException("Лента кадров шире предела JPEG (65 535 пикселей).", nameof(tiles));
        }

        using var target = NewCanvasBitmap((int)width, tileHeight);
        using (var canvas = new SKCanvas(target))
        {
            canvas.Clear(SKColors.Black);
            for (var i = 0; i < tiles.Count; i++)
            {
                using var tile = ImageDecoder.DecodeRgba(tiles[i]);
                using var image = SKImage.FromBitmap(tile);
                canvas.DrawImage(
                    image,
                    new SKRect(0, 0, tile.Width, tile.Height),
                    new SKRect(i * tileWidth, 0, (i + 1) * tileWidth, tileHeight),
                    new SKSamplingOptions(SKCubicResampler.Mitchell));
            }
        }

        return EncodeJpeg(target, quality, "ленту кадров");
    }

    /// <inheritdoc />
    /// <remarks>Обработка по явному запросу оператора (ТФ-МЕД-14) — новая картинка; вырезки выдачи выше её не используют.</remarks>
    public byte[] ApplyEdits(byte[] imageBytes, ImageEdit edit) => ImageEditing.ApplyEdits(imageBytes, edit);

    /// <inheritdoc />
    public byte[] RenderAnnotated(byte[] imageBytes, IReadOnlyList<AnnotationRow> annotations, string? caption) =>
        ImageEditing.RenderAnnotated(imageBytes, annotations, caption);

    /// <inheritdoc />
    /// <remarks>
    /// Снимок кладётся на белый холст: прозрачный PNG в JPEG иначе стал бы чёрным. Масштаб — только вниз и только с
    /// сохранением пропорций (ТЭ-007); маленький снимок отдаётся в своём размере — его растягивает сам лист.
    /// </remarks>
    public byte[] FitJpeg(byte[] imageBytes, int maxWidth, int maxHeight, int quality = 90)
    {
        ArgumentNullException.ThrowIfNull(imageBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxHeight, 1);

        using var source = ImageEditing.DecodeOriented(imageBytes, SKColors.White);
        var scale = Math.Min(1f, Math.Min((float)maxWidth / source.Width, (float)maxHeight / source.Height));
        var width = Math.Max(1, (int)MathF.Round(source.Width * scale));
        var height = Math.Max(1, (int)MathF.Round(source.Height * scale));
        using var target = NewCanvasBitmap(width, height);
        using (var canvas = new SKCanvas(target))
        using (var image = SKImage.FromBitmap(source))
        {
            canvas.Clear(SKColors.White);
            canvas.DrawImage(
                image,
                new SKRect(0, 0, source.Width, source.Height),
                new SKRect(0, 0, width, height),
                new SKSamplingOptions(SKCubicResampler.Mitchell));
        }

        return EncodeJpeg(target, quality, "снимок фототаблицы");
    }

    /// <inheritdoc />
    public byte[] RenderPhotoSheetPdf(PhotoSheetDocument document) => PhotoSheetPdf.Render(document);

    private static SKBitmap NewCanvasBitmap(int width, int height) =>
        new(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));

    private static byte[] EncodeJpeg(SKBitmap bitmap, int quality, string what)
    {
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Jpeg, Math.Clamp(quality, 1, 100))
            ?? throw new InvalidOperationException($"Не удалось закодировать {what} в JPEG.");
        return encoded.ToArray();
    }
}
