using System;
using System.Linq;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using ISC.AI.Vision.Onnx.Imaging;
using Shouldly;
using SkiaSharp;
using Xunit;

namespace ISC.AI.UnitTests.Media;

/// <summary>
/// Оценка кадра для подсказки эталона (ТФ-МЕД-21, ADR-0058): поза по пяти точкам лица (синтетические точки, без
/// реальных лиц), частные оценки и итог, ранжирование кадров, подписи для карточек; настоящий Skia — резкость чёткого
/// выше размытого, тёмное освещено хуже среднего.
/// </summary>
public sealed class FrameQualityTests
{
    // Анфас: глаза на одной линии, нос посередине на 55 % пути от линии глаз к линии рта.
    private static readonly FaceLandmarks Frontal = new(
        new FacePoint(100, 100), new FacePoint(160, 100), new FacePoint(130, 133), new FacePoint(110, 160), new FacePoint(150, 160));

    [Fact(DisplayName = "Поза: анфас — около нуля; нос в сторону — поворот; нос ниже — кивок; наклон линии глаз — наклон, без ложного поворота")]
    public void Pose_from_landmarks()
    {
        var frontal = FrameQuality.EstimatePose(Frontal);
        frontal.YawDegrees.ShouldBe(0, 1);
        frontal.PitchDegrees.ShouldBe(0, 1);
        frontal.RollDegrees.ShouldBe(0, 0.01);

        var turned = FrameQuality.EstimatePose(Frontal with { Nose = new FacePoint(150, 133) });
        MathF.Abs(turned.YawDegrees).ShouldBeGreaterThan(30);

        var down = FrameQuality.EstimatePose(Frontal with { Nose = new FacePoint(130, 150) });
        down.PitchDegrees.ShouldBeGreaterThan(15);

        // Тот же анфас, повёрнутый на 30° вокруг середины глаз: наклон 30°, поворот и кивок — около нуля.
        var tilted = FrameQuality.EstimatePose(Rotate(Frontal, 30, 130, 100));
        tilted.RollDegrees.ShouldBe(30, 0.5);
        tilted.YawDegrees.ShouldBe(0, 1);
        tilted.PitchDegrees.ShouldBe(0, 1);
    }

    [Fact(DisplayName = "Частные оценки: резкость насыщается, свет — середина шкалы и контраст, поза обнуляется к 60°/40°, размер — до 80 пикселей")]
    public void Partial_scores()
    {
        FrameQuality.SharpnessScore(0).ShouldBe(0);
        FrameQuality.SharpnessScore(FrameQuality.SharpnessHalf).ShouldBe(0.5f, 1e-6f);
        FrameQuality.SharpnessScore(1500).ShouldBeGreaterThan(0.9f);

        FrameQuality.LightingScore(128, 50).ShouldBe(1f, 1e-6f);
        FrameQuality.LightingScore(20, 10).ShouldBeLessThan(FrameQuality.LightingScore(128, 10));
        FrameQuality.LightingScore(250, 50).ShouldBeLessThan(0.5f);

        FrameQuality.PoseScore(new FacePose(0, 0, 45)).ShouldBe(1f); // наклон к плечу выравнивается — позу не портит
        FrameQuality.PoseScore(new FacePose(30, 0, 0)).ShouldBe(0.5f, 1e-6f);
        FrameQuality.PoseScore(new FacePose(0, 40, 0)).ShouldBe(0f);

        FrameQuality.SizeScore(40).ShouldBe(0.5f);
        FrameQuality.SizeScore(200).ShouldBe(1f);
    }

    [Fact(DisplayName = "Итог: взвешенная сумма частных оценок; у непригодного лица (ТО-мат-07) — 0, эталоном оно быть не может")]
    public void Evaluate_combines_and_zeroes_unusable()
    {
        var stats = new FaceImageStats(LaplacianVariance: 150, MeanLuma: 128, LumaStdDev: 50);
        var good = FrameQuality.Evaluate(Frontal, stats, acceptable: true);

        good.Sharpness.ShouldBe(0.5f, 1e-3f);
        good.Lighting.ShouldBe(1f, 1e-3f);
        good.EyeDistance.ShouldBe(60f, 1e-3f);
        var expected = (FrameQuality.SharpnessWeight * 0.5f) + (FrameQuality.PoseWeight * good.Pose)
            + (FrameQuality.LightingWeight * 1f) + (FrameQuality.SizeWeight * 0.75f);
        good.Score.ShouldBe(expected, 2e-3f);
        (FrameQuality.SharpnessWeight + FrameQuality.PoseWeight + FrameQuality.LightingWeight + FrameQuality.SizeWeight).ShouldBe(1f, 1e-6f);

        FrameQuality.Evaluate(Frontal, stats, acceptable: false).Score.ShouldBe(0);
    }

    [Fact(DisplayName = "Ранжирование: пригодные первыми; с оценками — по оценке кадра; без оценок — после, по общему баллу; затем по номеру")]
    public void Ranking_order()
    {
        FaceRow[] faces =
        [
            Face(1, acceptable: false, quality: 0.99f, score: 0.9f),
            Face(2, acceptable: true, quality: 0.95f, score: null),
            Face(3, acceptable: true, quality: 0.60f, score: 0.70f),
            Face(4, acceptable: true, quality: 0.90f, score: 0.40f),
            Face(5, acceptable: true, quality: 0.97f, score: null),
            Face(6, acceptable: true, quality: 0.60f, score: 0.70f),
        ];

        FaceRanking.Order(faces).Select(f => f.Id).ShouldBe([3, 6, 4, 5, 2, 1]);
        FaceRanking.Best(faces)!.Id.ShouldBe(3);
        FaceRanking.Best([]).ShouldBeNull();
    }

    [Fact(DisplayName = "Подписи: оценка процентом, частные оценки и ≈ градусы; без оценок — подсказка переиндексировать")]
    public void Labels()
    {
        var metrics = new FrameMetrics(0.824f, 0.75f, -12.4f, 4.6f, 2f, 64.2f, 0.781f);

        FrameLabels.Score(metrics).ShouldBe("оценка кадра 78 %");
        FrameLabels.Details(metrics).ShouldBe("резкость 82 % · поворот ≈ 12° · кивок ≈ 5° · освещённость 75 % · между глазами 64 пикс.");
        FrameLabels.Score(null).ShouldBe(FrameLabels.NoMetrics);
        FrameLabels.Details(null).ShouldBe(FrameLabels.NoMetrics);
    }

    [Fact(DisplayName = "Skia: резкость чёткой шахматки выше размытой; тёмное лицо — низкая яркость; рамка мимо изображения — отказ")]
    public void Skia_measures_sharpness_and_light()
    {
        var tools = new SkiaImageTools();
        var box = new BoundingBox(20, 20, 120, 120);
        var sharp = Image(160, 160, (x, y) => ((x / 8) + (y / 8)) % 2 == 0 ? SKColors.White : SKColors.Black);
        var blurred = Blur(sharp);
        var dark = Image(160, 160, (_, _) => new SKColor(20, 20, 20));

        var sharpStats = tools.MeasureFace(sharp, box);
        var blurredStats = tools.MeasureFace(blurred, box);
        sharpStats.LaplacianVariance.ShouldBeGreaterThan(blurredStats.LaplacianVariance * 2);
        sharpStats.MeanLuma.ShouldBe(128, 20);
        sharpStats.LumaStdDev.ShouldBeGreaterThan(100);

        var darkStats = tools.MeasureFace(dark, box);
        darkStats.MeanLuma.ShouldBe(20, 2);
        FrameQuality.LightingScore(darkStats.MeanLuma, darkStats.LumaStdDev)
            .ShouldBeLessThan(FrameQuality.LightingScore(sharpStats.MeanLuma, sharpStats.LumaStdDev));

        Should.Throw<ArgumentException>(() => tools.MeasureFace(sharp, new BoundingBox(500, 500, 10, 10)));
    }

    private static FaceRow Face(int id, bool acceptable, float quality, float? score) =>
        new(id, 5, null, null, 0, 0, 10, 10, 0.9f, quality, acceptable, null, null, null, 0, 1,
            score is { } s ? new FrameMetrics(0.5f, 0.5f, 0, 0, 0, 60, s) : null);

    private static FaceLandmarks Rotate(FaceLandmarks landmarks, float degrees, float cx, float cy)
    {
        var radians = degrees * MathF.PI / 180f;
        FacePoint Turn(FacePoint p) => new(
            cx + ((p.X - cx) * MathF.Cos(radians)) - ((p.Y - cy) * MathF.Sin(radians)),
            cy + ((p.X - cx) * MathF.Sin(radians)) + ((p.Y - cy) * MathF.Cos(radians)));
        return new FaceLandmarks(
            Turn(landmarks.LeftEye), Turn(landmarks.RightEye), Turn(landmarks.Nose), Turn(landmarks.LeftMouth), Turn(landmarks.RightMouth));
    }

    private static byte[] Image(int width, int height, Func<int, int, SKColor> color)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                bitmap.SetPixel(x, y, color(x, y));
            }
        }

        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    // Размытие гауссом (синтетический «нерезкий кадр»).
    private static byte[] Blur(byte[] png)
    {
        using var source = SKBitmap.Decode(png);
        using var target = new SKBitmap(new SKImageInfo(source.Width, source.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(target))
        using (var image = SKImage.FromBitmap(source))
        using (var paint = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(4, 4) })
        {
            canvas.DrawImage(image, 0, 0, new SKSamplingOptions(SKFilterMode.Linear), paint);
        }

        using var data = target.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
