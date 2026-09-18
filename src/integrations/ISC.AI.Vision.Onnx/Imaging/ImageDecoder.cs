using SkiaSharp;

namespace ISC.AI.Vision.Onnx.Imaging;

/// <summary>Декодирование изображений (SkiaSharp, MIT): байты → растр RGBA8888 известного формата.</summary>
internal static class ImageDecoder
{
    /// <summary>Декодирует JPEG/PNG/BMP/WebP/GIF в RGBA8888. Неподдержанный формат — явная ошибка.</summary>
    public static SKBitmap DecodeRgba(byte[] imageBytes)
    {
        ArgumentNullException.ThrowIfNull(imageBytes);

        using var decoded = SKBitmap.Decode(imageBytes)
            ?? throw new InvalidOperationException("Изображение не распознано: формат не поддерживается или файл повреждён.");

        // Приводим к единому формату: декодер на разных ОС отдаёт BGRA/RGBA по-своему.
        var rgba = decoded.Copy(SKColorType.Rgba8888)
            ?? throw new InvalidOperationException("Не удалось привести изображение к формату RGBA.");
        return rgba;
    }

    /// <summary>
    /// Масштабирует растр в <paramref name="scale"/> раз (пропорции сохраняются); всегда возвращает НОВЫЙ
    /// растр (при масштабе 1 — копию), чтобы владение и освобождение были однозначными.
    /// </summary>
    public static SKBitmap ScaleBy(SKBitmap source, float scale)
    {
        if (scale <= 0f)
        {
            throw new ArgumentOutOfRangeException(nameof(scale), "Масштаб должен быть положительным.");
        }

        var info = new SKImageInfo(
            Math.Max(1, (int)Math.Round(source.Width * scale)),
            Math.Max(1, (int)Math.Round(source.Height * scale)),
            SKColorType.Rgba8888, SKAlphaType.Premul);
        return source.Resize(info, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear))
            ?? throw new InvalidOperationException("Не удалось масштабировать изображение.");
    }
}
