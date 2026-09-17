using System;
using System.Collections.Generic;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Vision.Onnx.Detection;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Vision;

/// <summary>
/// Пост-обработка YuNet без модели: декодирование клетки сетки в рамку/точки по формулам OpenCV
/// и подавление дублей. Ошибка здесь — систематически сдвинутые рамки на всех кадрах, поэтому
/// формулы закреплены числами.
/// </summary>
public sealed class YuNetDecoderTests
{
    private static Dictionary<string, float[]> EmptyOutputs(int width, int height)
    {
        var outputs = new Dictionary<string, float[]>();
        foreach (var stride in YuNetDecoder.Strides)
        {
            var cells = (width / stride) * (height / stride);
            outputs[$"cls_{stride}"] = new float[cells];
            outputs[$"obj_{stride}"] = new float[cells];
            outputs[$"bbox_{stride}"] = new float[cells * 4];
            outputs[$"kps_{stride}"] = new float[cells * 10];
        }

        return outputs;
    }

    [Fact(DisplayName = "Клетка (c=3, r=2) шага 16 с балл √(cls·obj) декодируется в рамку и точки по формулам OpenCV")]
    public void Cell_decodes_to_box_and_landmarks()
    {
        var outputs = EmptyOutputs(64, 64); // шаг 16 → 4×4 клеток
        const int stride = 16, cols = 4, c = 3, r = 2;
        var idx = r * cols + c;
        outputs["cls_16"][idx] = 0.81f;
        outputs["obj_16"][idx] = 1.0f;                       // score = √0.81 = 0.9
        outputs["bbox_16"][idx * 4 + 0] = 0.5f;             // cx = (3 + 0.5) * 16 = 56
        outputs["bbox_16"][idx * 4 + 1] = 0.25f;            // cy = (2 + 0.25) * 16 = 36
        outputs["bbox_16"][idx * 4 + 2] = MathF.Log(2f);    // w = 2 * 16 = 32
        outputs["bbox_16"][idx * 4 + 3] = MathF.Log(1f);    // h = 1 * 16 = 16
        outputs["kps_16"][idx * 10 + 0] = 0.1f;             // левый глаз x = (3 + 0.1) * 16 = 49.6
        outputs["kps_16"][idx * 10 + 1] = 0.2f;             // y = (2 + 0.2) * 16 = 35.2

        var faces = YuNetDecoder.Decode(outputs, 64, 64, scoreThreshold: 0.5f);

        var face = faces.ShouldHaveSingleItem();
        face.Score.ShouldBe(0.9f, 1e-5f);
        face.Box.X.ShouldBe(56f - 16f, 1e-4f);
        face.Box.Y.ShouldBe(36f - 8f, 1e-4f);
        face.Box.Width.ShouldBe(32f, 1e-4f);
        face.Box.Height.ShouldBe(16f, 1e-4f);
        face.Landmarks.LeftEye.X.ShouldBe(49.6f, 1e-4f);
        face.Landmarks.LeftEye.Y.ShouldBe(35.2f, 1e-4f);
    }

    [Fact(DisplayName = "Балл ниже порога — клетка отбрасывается")]
    public void Low_score_cells_are_dropped()
    {
        var outputs = EmptyOutputs(32, 32);
        outputs["cls_8"][0] = 0.5f;
        outputs["obj_8"][0] = 0.5f; // score = 0.5

        YuNetDecoder.Decode(outputs, 32, 32, scoreThreshold: 0.9f).ShouldBeEmpty();
        YuNetDecoder.Decode(outputs, 32, 32, scoreThreshold: 0.4f).ShouldHaveSingleItem();
    }

    [Fact(DisplayName = "Отсутствующий выход модели — явная ошибка «файл модели не тот»")]
    public void Missing_output_is_explicit_error()
    {
        var outputs = EmptyOutputs(32, 32);
        outputs.Remove("kps_32");

        var error = Should.Throw<InvalidOperationException>(() => YuNetDecoder.Decode(outputs, 32, 32, 0.5f));
        error.Message.ShouldContain("kps_32");
    }

    [Fact(DisplayName = "NMS: из двух почти совпадающих рамок остаётся более уверенная; далёкая — сохраняется")]
    public void Nms_keeps_best_of_overlapping_and_all_distinct()
    {
        var lm = new FaceLandmarks(new(0, 0), new(1, 0), new(0, 1), new(0, 2), new(1, 2));
        var strong = new DetectedFace(new BoundingBox(10, 10, 40, 40), lm, 0.95f);
        var duplicate = new DetectedFace(new BoundingBox(12, 11, 40, 40), lm, 0.80f);
        var far = new DetectedFace(new BoundingBox(200, 200, 30, 30), lm, 0.70f);

        var kept = YuNetDecoder.NonMaximumSuppression([duplicate, far, strong], iouThreshold: 0.3f);

        kept.Count.ShouldBe(2);
        kept[0].ShouldBe(strong);
        kept[1].ShouldBe(far);
    }

    [Fact(DisplayName = "IoU: совпадающие рамки — 1, непересекающиеся — 0, половинное перекрытие — 1/3")]
    public void Iou_values()
    {
        var a = new BoundingBox(0, 0, 10, 10);
        YuNetDecoder.IntersectionOverUnion(a, a).ShouldBe(1f, 1e-6f);
        YuNetDecoder.IntersectionOverUnion(a, new BoundingBox(20, 20, 10, 10)).ShouldBe(0f);
        YuNetDecoder.IntersectionOverUnion(a, new BoundingBox(5, 0, 10, 10)).ShouldBe(1f / 3f, 1e-6f);
    }
}
