using System;
using System.Collections.Generic;
using System.Linq;

namespace ISC.AI.Modules.Media.Domain.Model;

/// <summary>Вид пометки на изображении (ТФ-МЕД-13, ADR-0050).</summary>
public enum AnnotationKind
{
    /// <summary>Стрелка от первой точки ко второй (остриё — у второй).</summary>
    Arrow = 1,

    /// <summary>Рамка по двум противоположным углам.</summary>
    Rectangle = 2,

    /// <summary>Подпись: текст у первой точки.</summary>
    Text = 3,
}

/// <summary>
/// Пометка на фотографии (ТФ-МЕД-13, ADR-0050): стрелка, рамка или подпись в пикселях изображения — в том виде, как его
/// показывает браузер (с поворотом по EXIF). Слой поверх оригинала: сам файл не меняется (ТЭ-007).
/// </summary>
/// <param name="Id">Пометка.</param>
/// <param name="AssetId">Носитель (фото).</param>
/// <param name="Kind">Вид.</param>
/// <param name="X1">Первая точка, X.</param>
/// <param name="Y1">Первая точка, Y.</param>
/// <param name="X2">Вторая точка, X (у подписи — та же, что первая).</param>
/// <param name="Y2">Вторая точка, Y.</param>
/// <param name="Text">Текст подписи (у стрелки и рамки — необязательная подпись рядом).</param>
/// <param name="Color">Цвет из палитры <see cref="AnnotationColors"/>.</param>
/// <param name="AuthorUserId">Кто поставил.</param>
/// <param name="CreatedAt">Когда (UTC).</param>
public sealed record AnnotationRow(
    int Id, int AssetId, AnnotationKind Kind, double X1, double Y1, double X2, double Y2, string? Text, string Color,
    int? AuthorUserId, DateTime CreatedAt);

/// <summary>Новая пометка.</summary>
/// <param name="AssetId">Носитель (фото).</param>
/// <param name="Kind">Вид.</param>
/// <param name="X1">Первая точка, X.</param>
/// <param name="Y1">Первая точка, Y.</param>
/// <param name="X2">Вторая точка, X.</param>
/// <param name="Y2">Вторая точка, Y.</param>
/// <param name="Text">Текст подписи.</param>
/// <param name="Color">Цвет из палитры.</param>
public sealed record AnnotationDraft(
    int AssetId, AnnotationKind Kind, double X1, double Y1, double X2, double Y2, string? Text, string Color)
{
    /// <summary>Наибольшая длина текста подписи.</summary>
    public const int MaxTextLength = 200;

    /// <summary>Наименьший размер стрелки или рамки, пикселей: случайный щелчок пометкой не считается.</summary>
    public const double MinSize = 3;

    /// <summary>Ошибки пометки; пусто — допустима.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (!Enum.IsDefined(Kind))
        {
            errors.Add("Неизвестный вид пометки.");
        }

        if (!new[] { X1, Y1, X2, Y2 }.All(double.IsFinite) || new[] { X1, Y1, X2, Y2 }.Any(v => v < 0 || v > 1_000_000))
        {
            errors.Add("Координаты пометки — вне изображения.");
        }
        else if (Kind != AnnotationKind.Text && Math.Max(Math.Abs(X2 - X1), Math.Abs(Y2 - Y1)) < MinSize)
        {
            errors.Add("Пометка слишком мала — протяните мышью.");
        }

        if (Kind == AnnotationKind.Text && string.IsNullOrWhiteSpace(Text))
        {
            errors.Add("Введите текст подписи.");
        }

        if (Text is { Length: > MaxTextLength })
        {
            errors.Add($"Текст подписи — не длиннее {MaxTextLength} знаков.");
        }

        if (!AnnotationColors.IsAllowed(Color))
        {
            errors.Add("Цвет — из палитры.");
        }

        return errors;
    }
}

/// <summary>Палитра пометок: заметные на фотографиях цвета; иные значения не принимаются (в разметку попадает только они).</summary>
public static class AnnotationColors
{
    /// <summary>Красный — по умолчанию.</summary>
    public const string Red = "#E53935";

    /// <summary>Жёлтый.</summary>
    public const string Yellow = "#FDD835";

    /// <summary>Зелёный.</summary>
    public const string Green = "#43A047";

    /// <summary>Синий.</summary>
    public const string Blue = "#1E88E5";

    /// <summary>Белый.</summary>
    public const string White = "#FFFFFF";

    /// <summary>Вся палитра по порядку.</summary>
    public static IReadOnlyList<string> All { get; } = [Red, Yellow, Green, Blue, White];

    /// <summary>Цвет из палитры (без учёта регистра).</summary>
    public static bool IsAllowed(string? color) =>
        color is not null && All.Contains(color, StringComparer.OrdinalIgnoreCase);
}
