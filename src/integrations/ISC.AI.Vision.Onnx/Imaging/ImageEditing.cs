using System.Globalization;
using System.Runtime.InteropServices;
using ISC.AI.Modules.Media.Domain.Model;
using SkiaSharp;

namespace ISC.AI.Vision.Onnx.Imaging;

/// <summary>
/// Обработка фотографии и нанесение пометок на SkiaSharp (ТФ-МЕД-13/14, ADR-0050). Всё строится из ОРИГИНАЛА и
/// возвращает новую картинку (PNG без потерь); оригинал не меняется (ТЭ-007). Порядок операций — как у
/// <see cref="ImageEdit"/> (тот же, что у предпросмотра в браузере): поворот по EXIF, поворот, кадрирование,
/// увеличение, тон, резкость.
/// </summary>
internal static class ImageEditing
{
    /// <summary>Обработанная копия по набору операций; недопустимый набор — <see cref="ArgumentException"/> с причинами.</summary>
    public static byte[] ApplyEdits(byte[] imageBytes, ImageEdit edit)
    {
        ArgumentNullException.ThrowIfNull(imageBytes);
        ArgumentNullException.ThrowIfNull(edit);

        using var oriented = DecodeOriented(imageBytes);
        var errors = edit.Validate(oriented.Width, oriented.Height);
        if (errors.Count > 0)
        {
            throw new ArgumentException(string.Join(" ", errors), nameof(edit));
        }

        using var rotated = Transform(oriented, RotationOrigin(edit.Rotation));
        var crop = edit.Crop ?? new CropRect(0, 0, rotated.Width, rotated.Height);
        var (width, height) = (crop.Width * edit.Zoom, crop.Height * edit.Zoom);

        using var target = NewBitmap(width, height);
        using (var canvas = new SKCanvas(target))
        using (var image = SKImage.FromBitmap(rotated))
        {
            canvas.Clear(SKColors.Black);

            // Без увеличения — пиксель в пиксель (ничего не «досчитывается»); с увеличением — бикубически (Catmull-Rom).
            var sampling = edit.Zoom > 1
                ? new SKSamplingOptions(SKCubicResampler.CatmullRom)
                : new SKSamplingOptions(SKFilterMode.Nearest);
            canvas.DrawImage(
                image,
                new SKRect(crop.X, crop.Y, crop.X + crop.Width, crop.Y + crop.Height),
                new SKRect(0, 0, width, height),
                sampling);
        }

        var pixels = target.Bytes;
        ApplyTone(pixels, edit);
        if (edit.Sharpen > 0)
        {
            pixels = ApplySharpen(pixels, width, height, target.RowBytes, edit.Sharpen / 100.0);
        }

        Marshal.Copy(pixels, 0, target.GetPixels(), pixels.Length);
        target.NotifyPixelsChanged();
        return EncodePng(target);
    }

    /// <summary>Изображение с нанесёнными пометками и полосой подписи внизу.</summary>
    public static byte[] RenderAnnotated(byte[] imageBytes, IReadOnlyList<AnnotationRow> annotations, string caption)
    {
        ArgumentNullException.ThrowIfNull(imageBytes);
        ArgumentNullException.ThrowIfNull(annotations);

        using var oriented = DecodeOriented(imageBytes);
        var (width, height) = (oriented.Width, oriented.Height);
        var unit = Math.Max(1f, Math.Min(width, height) / 400f);
        var captionHeight = (int)Math.Ceiling(Math.Max(28f, 14f * unit + 14f));

        // Шрифт — системный (Arial, иначе любой с кириллицей); общий экземпляр по умолчанию не освобождается.
        var typeface = Typeface();
        using var target = NewBitmap(width, height + captionHeight);
        using (var canvas = new SKCanvas(target))
        using (var image = SKImage.FromBitmap(oriented))
        {
            canvas.Clear(SKColors.Black);
            canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest));

            foreach (var annotation in annotations)
            {
                DrawAnnotation(canvas, annotation, unit, typeface);
            }

            using var captionFont = new SKFont(typeface, Math.Max(12f, captionHeight * 0.5f));
            using var captionPaint = new SKPaint { Color = SKColors.White, IsAntialias = true };
            canvas.DrawText(caption ?? string.Empty, 8f, height + captionHeight * 0.68f, SKTextAlign.Left, captionFont, captionPaint);
        }

        return EncodePng(target);
    }

    /// <summary>
    /// Таблица тона на канал: яркость (сдвиг b·2,55), контраст (множитель вокруг 128), гамма (v' = 255·(v/255)^(1/g)).
    /// Те же формулы — у предпросмотра в браузере (media.js), поэтому копия совпадает с тем, что видел оператор.
    /// </summary>
    internal static byte[] ToneTable(int brightness, int contrast, double gamma)
    {
        var table = new byte[256];
        var c = contrast * 2.55;
        var factor = 259.0 * (c + 255.0) / (255.0 * (259.0 - c));
        for (var i = 0; i < 256; i++)
        {
            var v = i + brightness * 2.55;
            v = factor * (v - 128.0) + 128.0;
            v = Math.Clamp(v, 0.0, 255.0);
            v = 255.0 * Math.Pow(v / 255.0, 1.0 / gamma);
            table[i] = (byte)Math.Round(Math.Clamp(v, 0.0, 255.0), MidpointRounding.AwayFromZero);
        }

        return table;
    }

    private static void ApplyTone(byte[] rgba, ImageEdit edit)
    {
        if (edit.Brightness == 0 && edit.Contrast == 0 && Math.Abs(edit.Gamma - 1.0) < 0.005)
        {
            return;
        }

        var table = ToneTable(edit.Brightness, edit.Contrast, edit.Gamma);
        for (var i = 0; i + 3 < rgba.Length; i += 4)
        {
            rgba[i] = table[rgba[i]];
            rgba[i + 1] = table[rgba[i + 1]];
            rgba[i + 2] = table[rgba[i + 2]];
        }
    }

    /// <summary>Резкость — обычный фильтр 3×3: центр 1 + 4k, соседи по кресту −k; края — повтором крайних пикселей.</summary>
    internal static byte[] ApplySharpen(byte[] rgba, int width, int height, int rowBytes, double k)
    {
        var result = (byte[])rgba.Clone();
        var center = 1.0 + 4.0 * k;
        for (var y = 0; y < height; y++)
        {
            var up = Math.Max(0, y - 1) * rowBytes;
            var row = y * rowBytes;
            var down = Math.Min(height - 1, y + 1) * rowBytes;
            for (var x = 0; x < width; x++)
            {
                var left = Math.Max(0, x - 1) * 4;
                var self = x * 4;
                var right = Math.Min(width - 1, x + 1) * 4;
                for (var channel = 0; channel < 3; channel++)
                {
                    var value = center * rgba[row + self + channel]
                        - k * (rgba[up + self + channel] + rgba[down + self + channel] + rgba[row + left + channel] + rgba[row + right + channel]);
                    result[row + self + channel] = (byte)Math.Clamp(Math.Round(value), 0, 255);
                }
            }
        }

        return result;
    }

    /// <summary>Декодирование с поворотом по метке EXIF — как изображение показывает браузер (координаты пометок и кадрирования — в нём).</summary>
    internal static SKBitmap DecodeOriented(byte[] imageBytes)
    {
        using var data = SKData.CreateCopy(imageBytes);
        using var codec = SKCodec.Create(data)
            ?? throw new InvalidOperationException("Изображение не распознано: формат не поддерживается или файл повреждён.");
        var origin = codec.EncodedOrigin;
        using var decoded = SKBitmap.Decode(codec)
            ?? throw new InvalidOperationException("Изображение не распознано: формат не поддерживается или файл повреждён.");
        using var rgba = decoded.Copy(SKColorType.Rgba8888)
            ?? throw new InvalidOperationException("Не удалось привести изображение к формату RGBA.");
        return Transform(rgba, origin);
    }

    /// <summary>Поворот пользователя как метка ориентации: 90° — «RightTop», 180° — «BottomRight», 270° — «LeftBottom».</summary>
    private static SKEncodedOrigin RotationOrigin(int rotation) => rotation switch
    {
        90 => SKEncodedOrigin.RightTop,
        180 => SKEncodedOrigin.BottomRight,
        270 => SKEncodedOrigin.LeftBottom,
        _ => SKEncodedOrigin.TopLeft,
    };

    /// <summary>Новый растр с ориентацией <paramref name="origin"/>, пиксель в пиксель (ближайший сосед: перестановка, без пересчёта).</summary>
    private static SKBitmap Transform(SKBitmap source, SKEncodedOrigin origin)
    {
        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var width = swap ? source.Height : source.Width;
        var height = swap ? source.Width : source.Height;
        var (w, h) = ((float)width, (float)height);

        // x' = scaleX·x + skewX·y + transX; y' = skewY·x + scaleY·y + transY (в размерах результата).
        var matrix = origin switch
        {
            SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),
            SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightTop => new SKMatrix(0, -1, w, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, w, -1, 0, h, 0, 0, 1),
            SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, h, 0, 0, 1),
            _ => SKMatrix.Identity,
        };

        var target = NewBitmap(width, height);
        using var canvas = new SKCanvas(target);
        using var image = SKImage.FromBitmap(source);
        canvas.Clear(SKColors.Black);
        canvas.SetMatrix(matrix);
        canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest));
        return target;
    }

    private static void DrawAnnotation(SKCanvas canvas, AnnotationRow annotation, float unit, SKTypeface typeface)
    {
        var color = SKColor.TryParse(annotation.Color, out var parsed) ? parsed : SKColors.Red;
        using var stroke = new SKPaint
        {
            Color = color,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(2f, 2.5f * unit),
            StrokeCap = SKStrokeCap.Round,
        };
        var (x1, y1, x2, y2) = ((float)annotation.X1, (float)annotation.Y1, (float)annotation.X2, (float)annotation.Y2);
        switch (annotation.Kind)
        {
            case AnnotationKind.Rectangle:
                canvas.DrawRect(SKRect.Create(Math.Min(x1, x2), Math.Min(y1, y2), Math.Abs(x2 - x1), Math.Abs(y2 - y1)), stroke);
                break;
            case AnnotationKind.Arrow:
            {
                canvas.DrawLine(x1, y1, x2, y2, stroke);
                var angle = Math.Atan2(y2 - y1, x2 - x1);
                var head = 6f * stroke.StrokeWidth;
                foreach (var side in new[] { -0.45, 0.45 })
                {
                    canvas.DrawLine(
                        x2, y2,
                        x2 - head * (float)Math.Cos(angle + side), y2 - head * (float)Math.Sin(angle + side),
                        stroke);
                }

                break;
            }
            case AnnotationKind.Text:
            {
                using var font = new SKFont(typeface, Math.Max(14f, 14f * unit));
                using var fill = new SKPaint { Color = color, IsAntialias = true };
                using var back = new SKPaint { Color = new SKColor(0, 0, 0, 160) };
                var text = annotation.Text ?? string.Empty;
                var textWidth = font.MeasureText(text);
                canvas.DrawRect(SKRect.Create(x1 - 4, y1 - font.Size, textWidth + 8, font.Size * 1.3f), back);
                canvas.DrawText(text, x1, y1, SKTextAlign.Left, font, fill);
                break;
            }
        }
    }

    private static SKTypeface Typeface() =>
        SKFontManager.Default.MatchFamily("Arial") ?? SKFontManager.Default.MatchFamily(string.Empty) ?? SKTypeface.Default;

    private static SKBitmap NewBitmap(int width, int height) =>
        new(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));

    private static byte[] EncodePng(SKBitmap bitmap)
    {
        using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("Не удалось закодировать изображение в PNG.");
        return encoded.ToArray();
    }

    /// <summary>Число для подписи без зависимостей от культуры сервера.</summary>
    internal static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
