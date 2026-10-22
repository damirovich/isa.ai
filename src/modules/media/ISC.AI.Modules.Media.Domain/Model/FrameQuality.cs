using System;

namespace ISC.AI.Modules.Media.Domain.Model;

/// <summary>Измерения области лица на изображении (адаптер изображений): резкость и яркость.</summary>
/// <param name="LaplacianVariance">Дисперсия лапласиана яркости области лица, приведённой к 96 пикселям по ширине.</param>
/// <param name="MeanLuma">Средняя яркость (0–255).</param>
/// <param name="LumaStdDev">Разброс яркости — контраст (0–128).</param>
public readonly record struct FaceImageStats(float LaplacianVariance, float MeanLuma, float LumaStdDev);

/// <summary>Поворот головы по точкам лица, ≈ градусы (оценка для сравнения кадров, не измерение).</summary>
/// <param name="YawDegrees">Поворот влево-вправо.</param>
/// <param name="PitchDegrees">Кивок вверх-вниз.</param>
/// <param name="RollDegrees">Наклон к плечу (угол линии глаз).</param>
public readonly record struct FacePose(float YawDegrees, float PitchDegrees, float RollDegrees);

/// <summary>
/// Оценки кадра лица (ТФ-МЕД-21, ADR-0058): частные оценки 0–1, поза и итоговая оценка пригодности кадра для эталона.
/// Это оценка пригодности кадра, а НЕ вероятность тождества (ТЭ-006).
/// </summary>
/// <param name="Sharpness">Резкость, 0–1.</param>
/// <param name="Lighting">Освещённость, 0–1.</param>
/// <param name="YawDegrees">Поворот влево-вправо, ≈ градусы.</param>
/// <param name="PitchDegrees">Кивок, ≈ градусы.</param>
/// <param name="RollDegrees">Наклон, градусы.</param>
/// <param name="EyeDistance">Межзрачковое расстояние, пикселей.</param>
/// <param name="Score">Итоговая оценка кадра, 0–1; у непригодного лица (ТО-мат-07) — 0.</param>
public sealed record FrameMetrics(
    float Sharpness, float Lighting, float YawDegrees, float PitchDegrees, float RollDegrees, float EyeDistance, float Score)
{
    /// <summary>Оценка позы, 0–1.</summary>
    public float Pose => FrameQuality.PoseScore(new FacePose(YawDegrees, PitchDegrees, RollDegrees));

    /// <summary>Оценка размера лица, 0–1.</summary>
    public float Size => FrameQuality.SizeScore(EyeDistance);
}

/// <summary>
/// Оценка пригодности кадра для эталона (ТФ-МЕД-21, ADR-0058 п. 1): поза по пяти точкам лица детектора, частные оценки
/// и итог. Чистые функции — одни и те же при индексации и в проверках. Веса и нормировки — стартовые, калибруются на
/// пилоте вместе с порогами ТО-мат-07.
/// </summary>
public static class FrameQuality
{
    /// <summary>Дисперсия лапласиана, при которой резкость = 0,5.</summary>
    public const float SharpnessHalf = 150f;

    /// <summary>Поворот, при котором оценка позы обнуляется, ≈ градусы.</summary>
    public const float MaxYawDegrees = 60f;

    /// <summary>Кивок, при котором оценка позы обнуляется, ≈ градусы.</summary>
    public const float MaxPitchDegrees = 40f;

    /// <summary>Межзрачковое расстояние, начиная с которого размер не ограничивает пригодность, пикселей.</summary>
    public const float FullEyeDistance = 80f;

    /// <summary>Вес резкости в итоге.</summary>
    public const float SharpnessWeight = 0.35f;

    /// <summary>Вес позы в итоге.</summary>
    public const float PoseWeight = 0.30f;

    /// <summary>Вес освещённости в итоге.</summary>
    public const float LightingWeight = 0.15f;

    /// <summary>Вес размера в итоге.</summary>
    public const float SizeWeight = 0.20f;

    // Доля расстояния от линии глаз до линии рта, на которой нос стоит в анфас.
    private const float FrontalNoseRatio = 0.55f;

    /// <summary>
    /// Поза по точкам лица: наклон — угол линии глаз; поворот — смещение носа от середины глаз (в долях полурасстояния
    /// между глазами); кивок — положение носа между линией глаз и линией рта. Точки сначала доворачиваются на наклон.
    /// </summary>
    public static FacePose EstimatePose(FaceLandmarks landmarks)
    {
        var dx = landmarks.RightEye.X - landmarks.LeftEye.X;
        var dy = landmarks.RightEye.Y - landmarks.LeftEye.Y;
        var eyes = MathF.Sqrt((dx * dx) + (dy * dy));
        var roll = Degrees(MathF.Atan2(dy, dx));
        if (eyes < 1e-3f)
        {
            return new FacePose(0, 0, roll);
        }

        var (cos, sin) = (dx / eyes, dy / eyes);
        var midX = (landmarks.LeftEye.X + landmarks.RightEye.X) / 2;
        var midY = (landmarks.LeftEye.Y + landmarks.RightEye.Y) / 2;
        (float X, float Y) Local(float x, float y) => (((x - midX) * cos) + ((y - midY) * sin), (-(x - midX) * sin) + ((y - midY) * cos));

        var nose = Local(landmarks.Nose.X, landmarks.Nose.Y);
        var mouth = Local((landmarks.LeftMouth.X + landmarks.RightMouth.X) / 2, (landmarks.LeftMouth.Y + landmarks.RightMouth.Y) / 2);

        var yaw = Degrees(MathF.Asin(Math.Clamp(nose.X / (eyes / 2) * 0.9f, -1f, 1f)));
        var pitch = mouth.Y > 1e-3f
            ? Math.Clamp(((nose.Y / mouth.Y) - FrontalNoseRatio) / 0.35f, -1f, 1f) * 45f
            : 0f;
        return new FacePose(yaw, pitch, roll);
    }

    /// <summary>Резкость 0–1 по дисперсии лапласиана: насыщение <c>v / (v + 150)</c>.</summary>
    public static float SharpnessScore(float laplacianVariance) =>
        laplacianVariance <= 0 ? 0f : laplacianVariance / (laplacianVariance + SharpnessHalf);

    /// <summary>Освещённость 0–1: середина шкалы яркости лучше тёмного и пересвеченного, плюс достаточный контраст.</summary>
    public static float LightingScore(float meanLuma, float lumaStdDev) =>
        (0.6f * Math.Clamp(1f - (MathF.Abs(meanLuma - 128f) / 128f), 0f, 1f)) + (0.4f * Math.Clamp(lumaStdDev / 50f, 0f, 1f));

    /// <summary>Поза 0–1: анфас — 1; поворот к 60° или кивок к 40° — 0.</summary>
    public static float PoseScore(FacePose pose) =>
        Math.Max(0f, 1f - Math.Max(MathF.Abs(pose.YawDegrees) / MaxYawDegrees, MathF.Abs(pose.PitchDegrees) / MaxPitchDegrees));

    /// <summary>Размер 0–1: межзрачковое расстояние до 80 пикселей.</summary>
    public static float SizeScore(float eyeDistance) => Math.Clamp(eyeDistance / FullEyeDistance, 0f, 1f);

    /// <summary>
    /// Оценки кадра лица: частные оценки и итог; у непригодного лица (ТО-мат-07) итог — 0, эталоном оно быть не может.
    /// </summary>
    public static FrameMetrics Evaluate(FaceLandmarks landmarks, FaceImageStats stats, bool acceptable)
    {
        var pose = EstimatePose(landmarks);
        var sharpness = SharpnessScore(stats.LaplacianVariance);
        var lighting = LightingScore(stats.MeanLuma, stats.LumaStdDev);
        var eyeDistance = landmarks.InterocularDistance;
        var score = acceptable
            ? (SharpnessWeight * sharpness) + (PoseWeight * PoseScore(pose)) + (LightingWeight * lighting) + (SizeWeight * SizeScore(eyeDistance))
            : 0f;
        return new FrameMetrics(
            Round(sharpness), Round(lighting), Round(pose.YawDegrees), Round(pose.PitchDegrees), Round(pose.RollDegrees),
            Round(eyeDistance), Round(score));
    }

    private static float Degrees(float radians) => radians * 180f / MathF.PI;

    private static float Round(float value) => MathF.Round(value, 3);
}
