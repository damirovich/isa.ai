using System;
using System.Globalization;

namespace ISC.AI.Modules.Media.Domain.Model;

/// <summary>
/// Подписи оценок кадра (ТФ-МЕД-21, ADR-0058) для карточек носителя и фигуранта: проценты частных оценок и ≈ градусы
/// позы. Это пригодность кадра для эталона, а не вероятность тождества (ТЭ-006).
/// </summary>
public static class FrameLabels
{
    /// <summary>Подсказка для лица без оценок (проиндексировано до них).</summary>
    public const string NoMetrics = "оценок кадра нет — переиндексируйте носитель";

    /// <summary>Итоговая оценка кадра процентом: «оценка кадра 78 %».</summary>
    public static string Score(FrameMetrics? metrics) =>
        metrics is null ? NoMetrics : string.Create(CultureInfo.InvariantCulture, $"оценка кадра {Percent(metrics.Score)} %");

    /// <summary>Частные оценки одной строкой: резкость, поворот и кивок, освещённость, размер.</summary>
    public static string Details(FrameMetrics? metrics) =>
        metrics is null
            ? NoMetrics
            : string.Create(
                CultureInfo.InvariantCulture,
                $"резкость {Percent(metrics.Sharpness)} % · поворот ≈ {Degrees(metrics.YawDegrees)}° · кивок ≈ {Degrees(metrics.PitchDegrees)}° · освещённость {Percent(metrics.Lighting)} % · между глазами {Math.Round(metrics.EyeDistance):0} пикс.");

    private static int Percent(float value) => (int)Math.Round(Math.Clamp(value, 0f, 1f) * 100, MidpointRounding.AwayFromZero);

    private static int Degrees(float value) => (int)Math.Round(Math.Abs(value), MidpointRounding.AwayFromZero);
}
