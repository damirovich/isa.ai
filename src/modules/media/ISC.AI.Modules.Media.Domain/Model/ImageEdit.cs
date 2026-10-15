using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ISC.AI.Modules.Media.Domain.Model;

/// <summary>Прямоугольник кадрирования в пикселях изображения ПОСЛЕ поворота (левый верхний угол, ширина, высота).</summary>
/// <param name="X">Левый край.</param>
/// <param name="Y">Верхний край.</param>
/// <param name="Width">Ширина (≥ 1).</param>
/// <param name="Height">Высота (≥ 1).</param>
public sealed record CropRect(int X, int Y, int Width, int Height);

/// <summary>
/// Обработка фотографии (ТФ-МЕД-14, ADR-0050): набор операций, по которым из ОРИГИНАЛА строится обработанная копия.
/// Оригинал не меняется никогда (ТЭ-007): копия — отдельный носитель со ссылкой на источник и этим перечнем.
/// </summary>
/// <remarks>
/// <para>ПОРЯДОК применения — один и тот же в предпросмотре браузера и на сервере: (1) поворот по метке EXIF (как
/// изображение показывает браузер), (2) поворот <see cref="Rotation"/>, (3) кадрирование <see cref="Crop"/> (в
/// координатах после поворота), (4) увеличение <see cref="Zoom"/> (бикубическое), (5) тон — яркость, контраст, гамма
/// (таблица на канал), (6) резкость — обычный фильтр 3×3. «Улучшение нейросетью» не делается (ТЗ §3.3).</para>
/// <para>Формулы тона: яркость b ∈ [−100; 100] — сдвиг на b·2,55; контраст c ∈ [−100; 100] — множитель
/// f = 259·(C + 255) / (255·(259 − C)), C = c·2,55, вокруг 128; гамма g ∈ [0,3; 3] — v' = 255·(v/255)^(1/g).
/// Резкость s ∈ [0; 100] — ядро с центром 1 + 4k и соседями −k (k = s/100).</para>
/// </remarks>
/// <param name="Brightness">Яркость, −100…100 (0 — без изменения).</param>
/// <param name="Contrast">Контрастность, −100…100 (0 — без изменения).</param>
/// <param name="Gamma">Гамма, 0,3…3 (1 — без изменения).</param>
/// <param name="Sharpen">Резкость, 0…100 (0 — без изменения).</param>
/// <param name="Rotation">Поворот по часовой стрелке: 0, 90, 180 или 270 градусов.</param>
/// <param name="Crop">Кадрирование или <see langword="null"/> — всё изображение.</param>
/// <param name="Zoom">Увеличение: 1…4 (зум области лица — кадрирование по лицу и увеличение).</param>
public sealed record ImageEdit(
    int Brightness = 0,
    int Contrast = 0,
    double Gamma = 1.0,
    int Sharpen = 0,
    int Rotation = 0,
    CropRect? Crop = null,
    int Zoom = 1)
{
    /// <summary>Наибольший результат, пикселей: увеличение больше не даёт — память сервера не бесконечна.</summary>
    public const long MaxOutputPixels = 40_000_000;

    /// <summary>Наибольшее увеличение.</summary>
    public const int MaxZoom = 4;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Ни одной обработки: копия совпала бы с оригиналом по смыслу — сохранять нечего.</summary>
    public bool IsIdentity =>
        Brightness == 0 && Contrast == 0 && Math.Abs(Gamma - 1.0) < 0.005 && Sharpen == 0 && Rotation == 0 && Crop is null && Zoom == 1;

    /// <summary>Размер после поворота (ширина и высота меняются местами при 90° и 270°).</summary>
    public (int Width, int Height) RotatedSize(int width, int height) =>
        Rotation is 90 or 270 ? (height, width) : (width, height);

    /// <summary>Размер результата для изображения <paramref name="width"/>×<paramref name="height"/> (после EXIF).</summary>
    public (int Width, int Height) OutputSize(int width, int height)
    {
        var (w, h) = RotatedSize(width, height);
        if (Crop is { } crop)
        {
            (w, h) = (crop.Width, crop.Height);
        }

        return (w * Zoom, h * Zoom);
    }

    /// <summary>
    /// Ошибки набора для изображения <paramref name="width"/>×<paramref name="height"/> (после EXIF); пусто — набор
    /// допустим. Кадрирование должно лежать внутри повёрнутого изображения, результат — не больше <see cref="MaxOutputPixels"/>.
    /// </summary>
    public IReadOnlyList<string> Validate(int width, int height)
    {
        var errors = new List<string>();
        if (Brightness is < -100 or > 100)
        {
            errors.Add("Яркость — от −100 до 100.");
        }

        if (Contrast is < -100 or > 100)
        {
            errors.Add("Контрастность — от −100 до 100.");
        }

        if (!double.IsFinite(Gamma) || Gamma < 0.3 || Gamma > 3.0)
        {
            errors.Add("Гамма — от 0,3 до 3.");
        }

        if (Sharpen is < 0 or > 100)
        {
            errors.Add("Резкость — от 0 до 100.");
        }

        if (Rotation is not (0 or 90 or 180 or 270))
        {
            errors.Add("Поворот — на 0, 90, 180 или 270 градусов.");
        }

        if (Zoom is < 1 or > MaxZoom)
        {
            errors.Add($"Увеличение — от 1 до {MaxZoom}.");
        }

        var (w, h) = RotatedSize(width, height);
        if (Crop is { } crop
            && (crop.Width < 1 || crop.Height < 1 || crop.X < 0 || crop.Y < 0 || crop.X + crop.Width > w || crop.Y + crop.Height > h))
        {
            errors.Add("Область кадрирования выходит за границы изображения.");
        }

        var (outWidth, outHeight) = OutputSize(width, height);
        if ((long)outWidth * outHeight > MaxOutputPixels)
        {
            errors.Add("Результат слишком велик — уменьшите увеличение или область кадрирования.");
        }

        return errors;
    }

    /// <summary>
    /// Перечень операций для человека (ТЭ-007: применённые обработки называются рядом с изображением), по порядку
    /// применения: «поворот 90°; кадрирование 640×480 от (10, 20); увеличение ×2; яркость +20; гамма 1,4; резкость 30».
    /// </summary>
    public string Describe()
    {
        var parts = new List<string>();
        var ru = CultureInfo.GetCultureInfo("ru-RU");
        if (Rotation != 0)
        {
            parts.Add($"поворот {Rotation.ToString(CultureInfo.InvariantCulture)}°");
        }

        if (Crop is { } crop)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"кадрирование {crop.Width}×{crop.Height} от ({crop.X}, {crop.Y})"));
        }

        if (Zoom != 1)
        {
            parts.Add($"увеличение ×{Zoom.ToString(CultureInfo.InvariantCulture)}");
        }

        if (Brightness != 0)
        {
            parts.Add("яркость " + Signed(Brightness));
        }

        if (Contrast != 0)
        {
            parts.Add("контрастность " + Signed(Contrast));
        }

        if (Math.Abs(Gamma - 1.0) >= 0.005)
        {
            parts.Add("гамма " + Gamma.ToString("0.##", ru));
        }

        if (Sharpen != 0)
        {
            parts.Add("резкость " + Sharpen.ToString(CultureInfo.InvariantCulture));
        }

        return parts.Count == 0 ? "без обработки" : string.Join("; ", parts);
    }

    /// <summary>Набор в JSON — так он хранится в строке копии (воспроизводимость: из оригинала и набора получается та же копия).</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Набор из JSON строки копии; пусто или повреждено — <see langword="null"/>.</summary>
    public static ImageEdit? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ImageEdit>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Signed(int value) =>
        (value > 0 ? "+" : value < 0 ? "−" : string.Empty) + Math.Abs(value).ToString(CultureInfo.InvariantCulture);
}

/// <summary>Геометрия обработки фото (ADR-0050): прямоугольник после поворота и «зум лица».</summary>
public static class ImageEditGeometry
{
    /// <summary>Наименьшая длинная сторона лица после увеличения, пикселей: к ней подбирается «зум лица».</summary>
    public const int FaceTargetSide = 512;

    /// <summary>
    /// Прямоугольник изображения <paramref name="width"/>×<paramref name="height"/> в координатах после поворота на
    /// <paramref name="rotation"/>° по часовой (90: (x, y) → (H − y, x); 180: (W − x, H − y); 270: (y, W − x)).
    /// </summary>
    public static CropRect Rotate(CropRect rect, int rotation, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(rect);
        return rotation switch
        {
            90 => new CropRect(height - rect.Y - rect.Height, rect.X, rect.Height, rect.Width),
            180 => new CropRect(width - rect.X - rect.Width, height - rect.Y - rect.Height, rect.Width, rect.Height),
            270 => new CropRect(rect.Y, width - rect.X - rect.Width, rect.Height, rect.Width),
            _ => rect,
        };
    }

    /// <summary>
    /// «Зум лица» (ТФ-МЕД-14): рамка лица с полями <paramref name="margin"/> (доля стороны), обрезанная по краям
    /// изображения и переведённая в координаты после поворота; увеличение — чтобы длинная сторона стала не меньше
    /// <see cref="FaceTargetSide"/>, но не больше <see cref="ImageEdit.MaxZoom"/>.
    /// </summary>
    public static (CropRect Crop, int Zoom) FaceZoom(
        double x, double y, double faceWidth, double faceHeight, int rotation, int width, int height, double margin = 0.25)
    {
        var left = (int)Math.Floor(Math.Clamp(x - faceWidth * margin, 0, width - 1));
        var top = (int)Math.Floor(Math.Clamp(y - faceHeight * margin, 0, height - 1));
        var right = (int)Math.Ceiling(Math.Clamp(x + faceWidth * (1 + margin), left + 1, width));
        var bottom = (int)Math.Ceiling(Math.Clamp(y + faceHeight * (1 + margin), top + 1, height));
        var crop = Rotate(new CropRect(left, top, right - left, bottom - top), rotation, width, height);
        var longest = Math.Max(crop.Width, crop.Height);
        var zoom = Math.Clamp((int)Math.Ceiling((double)FaceTargetSide / longest), 1, ImageEdit.MaxZoom);
        return (crop, zoom);
    }
}
