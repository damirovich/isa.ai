using ISC.AI.Modules.Media.Domain.Model;

namespace ISC.AI.Vision.Onnx.Detection;

/// <summary>
/// Чистая пост-обработка выходов YuNet (без ONNX и растров — тестируется без модели): декодирование
/// «приоров» по трём шагам сетки (8/16/32) и подавление дублей (NMS). Формулы — из реализации
/// OpenCV <c>FaceDetectorYN</c>: центр = (клетка + смещение) × шаг, размер = exp(лог-размер) × шаг,
/// балл = √(cls × obj), ключевые точки = (клетка + смещение) × шаг.
/// </summary>
public static class YuNetDecoder
{
    /// <summary>Шаги сетки модели.</summary>
    public static readonly int[] Strides = [8, 16, 32];

    /// <summary>
    /// Декодирует выходы модели для входа размером <paramref name="inputWidth"/>×<paramref name="inputHeight"/>
    /// (уже дополненного до кратности 32). Ключи словаря — имена выходов ONNX (<c>cls_8</c>, <c>obj_8</c>,
    /// <c>bbox_8</c>, <c>kps_8</c> и т.д.). Возвращает кандидатов с баллом ≥ <paramref name="scoreThreshold"/>
    /// в координатах входа.
    /// </summary>
    public static List<DetectedFace> Decode(
        IReadOnlyDictionary<string, float[]> outputs, int inputWidth, int inputHeight, float scoreThreshold)
    {
        var faces = new List<DetectedFace>();
        foreach (var stride in Strides)
        {
            var cols = inputWidth / stride;
            var rows = inputHeight / stride;
            var cls = Require(outputs, $"cls_{stride}");
            var obj = Require(outputs, $"obj_{stride}");
            var bbox = Require(outputs, $"bbox_{stride}");
            var kps = Require(outputs, $"kps_{stride}");

            var cells = rows * cols;
            if (cls.Length < cells || obj.Length < cells || bbox.Length < cells * 4 || kps.Length < cells * 10)
            {
                throw new InvalidOperationException(
                    $"Выход YuNet для шага {stride} короче ожидаемого ({rows}×{cols} клеток): форма модели не совпадает с расчётной.");
            }

            for (var r = 0; r < rows; r++)
            {
                for (var c = 0; c < cols; c++)
                {
                    var idx = r * cols + c;
                    var score = MathF.Sqrt(Clamp01(cls[idx]) * Clamp01(obj[idx]));
                    if (score < scoreThreshold)
                    {
                        continue;
                    }

                    var cx = (c + bbox[idx * 4 + 0]) * stride;
                    var cy = (r + bbox[idx * 4 + 1]) * stride;
                    var w = MathF.Exp(bbox[idx * 4 + 2]) * stride;
                    var h = MathF.Exp(bbox[idx * 4 + 3]) * stride;
                    var box = new BoundingBox(cx - w / 2f, cy - h / 2f, w, h);

                    var k = idx * 10;
                    var landmarks = new FaceLandmarks(
                        Point(kps, k + 0, c, r, stride),
                        Point(kps, k + 2, c, r, stride),
                        Point(kps, k + 4, c, r, stride),
                        Point(kps, k + 6, c, r, stride),
                        Point(kps, k + 8, c, r, stride));

                    faces.Add(new DetectedFace(box, landmarks, score));
                }
            }
        }

        return faces;
    }

    /// <summary>Жадное подавление дублей: сортировка по баллу, отбрасывание пересекающихся с IoU ≥ порога.</summary>
    public static List<DetectedFace> NonMaximumSuppression(IReadOnlyList<DetectedFace> candidates, float iouThreshold)
    {
        var ordered = candidates.OrderByDescending(f => f.Score).ToList();
        var kept = new List<DetectedFace>();
        foreach (var candidate in ordered)
        {
            var suppressed = kept.Any(k => IntersectionOverUnion(k.Box, candidate.Box) >= iouThreshold);
            if (!suppressed)
            {
                kept.Add(candidate);
            }
        }

        return kept;
    }

    /// <summary>Мера пересечения рамок (0 — не пересекаются, 1 — совпадают).</summary>
    public static float IntersectionOverUnion(BoundingBox a, BoundingBox b)
    {
        var x1 = MathF.Max(a.X, b.X);
        var y1 = MathF.Max(a.Y, b.Y);
        var x2 = MathF.Min(a.Right, b.Right);
        var y2 = MathF.Min(a.Bottom, b.Bottom);
        var intersection = MathF.Max(0f, x2 - x1) * MathF.Max(0f, y2 - y1);
        var union = a.Area + b.Area - intersection;
        return union <= 0f ? 0f : intersection / union;
    }

    private static FacePoint Point(float[] kps, int offset, int c, int r, int stride) =>
        new((c + kps[offset]) * stride, (r + kps[offset + 1]) * stride);

    private static float Clamp01(float value) => MathF.Min(1f, MathF.Max(0f, value));

    private static float[] Require(IReadOnlyDictionary<string, float[]> outputs, string name) =>
        outputs.TryGetValue(name, out var tensor)
            ? tensor
            : throw new InvalidOperationException($"У модели YuNet нет ожидаемого выхода «{name}»: файл модели не тот (ТИ-004).");
}
