using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;

namespace ISC.AI.Vision.Onnx.Quality;

/// <summary>
/// Базовая оценка пригодности лица (ТО-мат-07): межзрачковое расстояние не меньше порога, уверенность
/// детектора не ниже порога, рамка внутри кадра. Резкость и поза — следующий шаг калибровки на пилоте;
/// пороги — из <see cref="VisionOptions"/>. Причина отказа — человекочитаемая, для оператора.
/// </summary>
public sealed class BasicFaceQualityAssessor(VisionOptions options) : IFaceQualityAssessor
{
    /// <inheritdoc />
    public FaceQuality Assess(DetectedFace face, int imageWidth, int imageHeight)
    {
        ArgumentNullException.ThrowIfNull(face);

        var interocular = face.Landmarks.InterocularDistance;
        if (interocular < options.MinInterocularDistance)
        {
            return new FaceQuality(
                Score: interocular / options.MinInterocularDistance,
                Acceptable: false,
                Reason: $"лицо слишком мелкое: межзрачковое расстояние {interocular:0} пикс. при минимуме {options.MinInterocularDistance:0}");
        }

        if (face.Score < options.MinDetectionScoreForQuality)
        {
            return new FaceQuality(face.Score, false, $"низкая уверенность детектора ({face.Score:0.00})");
        }

        var box = face.Box;
        if (box.X < 0 || box.Y < 0 || box.Right > imageWidth || box.Bottom > imageHeight)
        {
            return new FaceQuality(0.5f, false, "лицо обрезано границей кадра");
        }

        // Итоговый балл — уверенность детектора, скорректированная размером (крупнее — надёжнее, до 2× порога).
        var sizeFactor = MathF.Min(1f, interocular / (2f * options.MinInterocularDistance));
        return new FaceQuality(MathF.Min(1f, face.Score * (0.5f + 0.5f * sizeFactor)), true, null);
    }
}
